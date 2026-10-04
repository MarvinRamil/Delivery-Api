using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Hubs;
using MediatR;

// ICustomerIdentityResolver: resolves Bookings Customer.Id → Identity UserId for SignalR routing

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

/// <summary>
/// Command to mark a delivery stop as in transit.
/// </summary>
public record InTransitStopCommand(
    Guid BookingId,
    Guid StopId,
    Guid DriverId
) : IRequest<Result<BookingDto>>;

public class InTransitStopCommandHandler : IRequestHandler<InTransitStopCommand, Result<BookingDto>>
{
    private readonly IBookingRepository _bookingRepository;
    private readonly INotificationService _notificationService;
    private readonly ICustomerIdentityResolver _customerIdentityResolver;
    /// <summary>Mirrors booking-status changes into the booking's Matrix room (#78). Direct rather
    /// than outboxed because this fires after SaveChanges - see BookingChatStatusChanged.</summary>
    private readonly IDirectBusPublisher _chatPublisher;
    private readonly IFileStorageService _fileStorage;

    public InTransitStopCommandHandler(IBookingRepository bookingRepository, INotificationService notificationService, ICustomerIdentityResolver customerIdentityResolver, IFileStorageService fileStorage, IDirectBusPublisher chatPublisher)
    {
        _bookingRepository = bookingRepository;
        _notificationService = notificationService;
        _customerIdentityResolver = customerIdentityResolver;
        _chatPublisher = chatPublisher;
        _fileStorage = fileStorage;
    }

    public async Task<Result<BookingDto>> Handle(InTransitStopCommand request, CancellationToken ct)
    {
        var booking = await _bookingRepository.GetByIdAsync(request.BookingId, ct);
        if (booking == null)
            return Result.NotFound<BookingDto>("Booking not found");

        if (booking.SelectedDriverId != request.DriverId)
            return Result.Forbidden<BookingDto>("You do not have access to this booking");

        var stop = booking.Stops.FirstOrDefault(s => s.Id == request.StopId);
        if (stop == null)
            return Result.Fail<BookingDto>("Stop not found");

        if (stop.Status == StopStatus.InTransit)
            return Result.Fail<BookingDto>("Stop already marked as in transit");

        if (stop.Status == StopStatus.Arrived)
            return Result.Fail<BookingDto>("Stop is already arrived");

        if (stop.Status == StopStatus.Completed)
            return Result.Fail<BookingDto>("Stop is already completed");

        if (stop.Status != StopStatus.Pending)
            return Result.Fail<BookingDto>($"Cannot mark stop as in transit. Current status: {stop.Status}");

        var previousStatus = booking.Status;
        try
        {
            stop.MarkAsInTransit();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Fail<BookingDto>(ex.Message);
        }
        // Note: Booking status should not change when a stop is marked as in transit.
        // Status changes only occur when stops are completed (handled in CompleteStopCommandHandler).

        await _bookingRepository.SaveChangesAsync(ct);
        await BookingStopStatusFlow.PublishStopAndBookingStatusAsync(_notificationService, _customerIdentityResolver, _chatPublisher, booking, stop, previousStatus);

        var dto = await BookingImageUrlResolver.ResolveAsync(BookingMapper.ToDto(booking), _fileStorage, ct);
        return Result.Ok(dto);
    }
}
