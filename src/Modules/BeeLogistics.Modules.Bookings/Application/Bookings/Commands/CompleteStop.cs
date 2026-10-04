using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Notification.Application.Services;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Hubs;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging;

// ICustomerIdentityResolver: resolves Bookings Customer.Id → Identity UserId for SignalR routing

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

/// <summary>
/// Command to complete a delivery stop.
/// Follows Single Responsibility Principle (SRP).
/// </summary>
public record CompleteStopCommand(
    Guid BookingId,
    Guid StopId,
    Guid DriverId
) : IRequest<Result<BookingDto>>;

/// <summary>
/// Handler for completing a stop.
/// Follows Dependency Inversion Principle (DIP).
/// </summary>
public class CompleteStopCommandHandler : IRequestHandler<CompleteStopCommand, Result<BookingDto>>
{
    private readonly IBookingRepository _bookingRepository;
    private readonly ICustomerRepository _customerRepository;
    private readonly IBookingEmailService _bookingEmailService;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly INotificationService _notificationService;
    private readonly ICustomerIdentityResolver _customerIdentityResolver;
    /// <summary>Mirrors booking-status changes into the booking's Matrix room (#78). Direct rather
    /// than outboxed because this fires after SaveChanges - see BookingChatStatusChanged.</summary>
    private readonly IDirectBusPublisher _chatPublisher;
    private readonly ILogger<CompleteStopCommandHandler> _logger;
    private readonly IFileStorageService _fileStorage;

    public CompleteStopCommandHandler(
        IBookingRepository bookingRepository,
        ICustomerRepository customerRepository,
        IBookingEmailService bookingEmailService,
        IPublishEndpoint publishEndpoint,
        INotificationService notificationService,
        ICustomerIdentityResolver customerIdentityResolver,
        IDirectBusPublisher chatPublisher,
        ILogger<CompleteStopCommandHandler> logger,
        IFileStorageService fileStorage)
    {
        _bookingRepository = bookingRepository;
        _customerRepository = customerRepository;
        _bookingEmailService = bookingEmailService;
        _publishEndpoint = publishEndpoint;
        _notificationService = notificationService;
        _customerIdentityResolver = customerIdentityResolver;
        _chatPublisher = chatPublisher;
        _logger = logger;
        _fileStorage = fileStorage;
    }

    public async Task<Result<BookingDto>> Handle(CompleteStopCommand request, CancellationToken ct)
    {
        var booking = await _bookingRepository.GetByIdAsync(request.BookingId, ct);
        if (booking == null)
            return Result.NotFound<BookingDto>("Booking not found");

        if (booking.SelectedDriverId != request.DriverId)
            return Result.Forbidden<BookingDto>("You do not have access to this booking");

        var stop = booking.Stops.FirstOrDefault(s => s.Id == request.StopId);
        if (stop == null)
            return Result.Fail<BookingDto>("Stop not found");

        if (stop.Status == StopStatus.Completed)
            return Result.Fail<BookingDto>("Stop already completed");

        if (stop.Status != StopStatus.Arrived)
            return Result.Fail<BookingDto>("Stop must be marked as arrived before completion");

        // Mark stop as completed
        stop.MarkAsCompleted();

        var previousStatus = booking.Status;
        // Check if all stops are completed
        var allCompleted = booking.Stops.All(s => s.Status == StopStatus.Completed);
        if (allCompleted)
        {
            booking.MarkCompleted();
            
            // Set FinalFare to EstimatedFare if FinalFare is not already set
            // This ensures completed bookings always have a fare value for driver earnings
            if (!booking.FinalFare.HasValue && booking.EstimatedFare > 0)
            {
                booking.SetFinalFare(booking.EstimatedFare);
            }
            
            await _publishEndpoint.Publish(new BookingCompletedEvent
            {
                BookingId = booking.Id,
                DriverId = booking.SelectedDriverId!.Value,
                CustomerId = booking.CustomerId,
                CompletedAt = DateTime.UtcNow,
                FinalFare = booking.FinalFare ?? booking.EstimatedFare
            }, ct);

            // Send "delivery completed" email to customer (driver marked last stop complete)
            await SendDeliveredEmailAsync(booking, stop.CompletedAt ?? DateTime.UtcNow, ct);
        }
        else
        {
            BookingStopStatusFlow.ApplyDerivedBookingStatus(booking);
        }

        await _bookingRepository.SaveChangesAsync(ct);
        await BookingStopStatusFlow.PublishStopAndBookingStatusAsync(_notificationService, _customerIdentityResolver, _chatPublisher, booking, stop, previousStatus);

        var dto = await BookingImageUrlResolver.ResolveAsync(BookingMapper.ToDto(booking), _fileStorage, ct);
        return Result.Ok(dto);
    }

    private async Task SendDeliveredEmailAsync(Booking booking, DateTime deliveredAt, CancellationToken ct)
    {
        try
        {
            var customer = await _customerRepository.GetByIdAsync(booking.CustomerId, ct);
            if (customer == null || string.IsNullOrEmpty(customer.Email))
                return;
            await _bookingEmailService.SendBookingDeliveredAsync(
                customer.Email,
                customer.Name ?? "Customer",
                booking.BookingNumber,
                booking.DropoffLocation,
                deliveredAt,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send delivered email for booking {BookingNumber}", booking.BookingNumber);
        }
    }
}
