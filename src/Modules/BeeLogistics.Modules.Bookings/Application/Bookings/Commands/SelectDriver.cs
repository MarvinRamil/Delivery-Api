using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using MediatR;

// ICustomerIdentityResolver: resolves Bookings Customer.Id → Identity UserId for SignalR routing

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

/// <summary>
/// Command to select a specific driver for a booking.
/// Follows Single Responsibility Principle (SRP).
/// </summary>
public record SelectDriverCommand(
    Guid BookingId,
    Guid DriverId,
    Guid CustomerId
) : IRequest<Result<BookingDto>>;

/// <summary>
/// Handler for selecting a driver.
/// Follows Dependency Inversion Principle (DIP).
/// </summary>
public class SelectDriverCommandHandler : IRequestHandler<SelectDriverCommand, Result<BookingDto>>
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IDriverBookingOfferRepository _offerRepository;
    private readonly IFileStorageService _fileStorage;
    private readonly IPublishEndpoint _publishEndpoint;

    public SelectDriverCommandHandler(
        IBookingRepository bookingRepository,
        IDriverBookingOfferRepository offerRepository,
        IFileStorageService fileStorage,
        IPublishEndpoint publishEndpoint)
    {
        _bookingRepository = bookingRepository;
        _offerRepository = offerRepository;
        _fileStorage = fileStorage;
        _publishEndpoint = publishEndpoint;
    }

    public async Task<Result<BookingDto>> Handle(SelectDriverCommand request, CancellationToken ct)
    {
        var booking = await _bookingRepository.GetByIdAsync(request.BookingId, ct);
        if (booking == null)
            return Result.NotFound<BookingDto>("Booking not found");

        if (booking.CustomerId != request.CustomerId)
            return Result.Forbidden<BookingDto>("You do not have access to this booking");

        if (booking.Status != BookingStatus.Pending)
            return Result.Fail<BookingDto>("Booking is not pending");

        // Find or create offer for selected driver
        var existingOffer = await _offerRepository.FindAsync(
            o => o.BookingId == request.BookingId && o.DriverId == request.DriverId, ct);

        var offer = existingOffer.FirstOrDefault();
        if (offer == null)
        {
            // Create new offer for selected driver
            var expirationTime = DateTime.UtcNow.AddSeconds(60); // 60 second expiration
            offer = new DriverBookingOffer(
                request.BookingId,
                request.DriverId,
                expirationTime,
                null, // Distance will be calculated
                booking.FavouriteDriverId == request.DriverId, // Is favourite
                null, // Rating will be fetched
                null // Estimated arrival will be calculated
            );
            _offerRepository.Add(offer);
        }

        // Confirm driver (this will update booking status)
        booking.ConfirmDriver(request.DriverId);
        offer.Accept();

        // Seat the driver in the booking's chat room (#78). The customer-picks-a-driver path is
        // the easy one to forget; missing it leaves a driver who cannot see the chat at all.
        // Published BEFORE the save because the outbox stages onto BookingsDbContext and flushes
        // when it is saved - staging after the last save is a silent drop (see #27).
        await _publishEndpoint.Publish(new BookingChatDriverAssigned
        {
            BookingId = booking.Id,
            DriverId = request.DriverId,
            DriverName = null,
            AssignedAt = DateTime.UtcNow,
        }, ct);

        await _bookingRepository.SaveChangesAsync(ct);
        await _offerRepository.SaveChangesAsync(ct);

        var dto = await BookingImageUrlResolver.ResolveAsync(BookingMapper.ToDto(booking), _fileStorage, ct);
        return Result.Ok(dto);
    }
}
