using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Hubs;
using BeeLogistics.Modules.Notification.Application.Services;
using MediatR;
using Microsoft.Extensions.Logging;
using MassTransit;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record UpdateBookingStatusCommand(Guid Id, BookingStatus Status, Guid CallerUserId, bool IsElevated) : IRequest<Result<BookingDto>>;

public class UpdateBookingStatusCommandHandler : IRequestHandler<UpdateBookingStatusCommand, Result<BookingDto>>
{
    private readonly IBookingRepository _repository;
    private readonly ICustomerRepository _customerRepository;
    private readonly IBookingEmailService _bookingEmailService;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly INotificationService _notificationService;
    private readonly ICustomerIdentityResolver _customerIdentityResolver;
    /// <summary>Mirrors booking-status changes into the booking's Matrix room (#78). Direct rather
    /// than outboxed because this fires after SaveChanges - see BookingChatStatusChanged.</summary>
    private readonly IDirectBusPublisher _chatPublisher;
    private readonly IFileStorageService _fileStorage;
    private readonly IBookingAccessPolicy _accessPolicy;
    private readonly ILogger<UpdateBookingStatusCommandHandler> _logger;

    public UpdateBookingStatusCommandHandler(
        IBookingRepository repository,
        ICustomerRepository customerRepository,
        IBookingEmailService bookingEmailService,
        IPublishEndpoint publishEndpoint,
        INotificationService notificationService,
        ICustomerIdentityResolver customerIdentityResolver,
        IDirectBusPublisher chatPublisher,
        IFileStorageService fileStorage,
        IBookingAccessPolicy accessPolicy,
        ILogger<UpdateBookingStatusCommandHandler> logger)
    {
        _repository = repository;
        _customerRepository = customerRepository;
        _bookingEmailService = bookingEmailService;
        _publishEndpoint = publishEndpoint;
        _notificationService = notificationService;
        _customerIdentityResolver = customerIdentityResolver;
        _chatPublisher = chatPublisher;
        _fileStorage = fileStorage;
        _accessPolicy = accessPolicy;
        _logger = logger;
    }

    public async Task<Result<BookingDto>> Handle(UpdateBookingStatusCommand request, CancellationToken ct)
    {
        var booking = await _repository.GetByIdAsync(request.Id, ct);
        if (booking == null) return Result.NotFound<BookingDto>("Booking not found");

        // Before any Mark*() call: the Completed branch settles the fare and credits driver
        // earnings, so a denied caller must not get as far as mutating the aggregate.
        if (!await _accessPolicy.CanAccessAsync(booking, request.CallerUserId, request.IsElevated, ct))
            return Result.Forbidden<BookingDto>("You do not have access to this booking");

        var previousStatus = booking.Status;

        // Handle new status names
        if (request.Status == BookingStatus.DriverAssigned)
            booking.MarkDriverEnRoute();
        else if (request.Status == BookingStatus.PickedUp)
            booking.MarkPickedUp();
        else if (request.Status == BookingStatus.InTransit)
            booking.MarkInTransit();
        // Handle legacy status names (for backward compatibility)
        else if (request.Status == BookingStatus.Dispatched)
            booking.MarkAsDispatched();
        else if (request.Status == BookingStatus.OnTheWayToPickup)
            booking.MarkAsOnTheWayToPickup();
        else if (request.Status == BookingStatus.InProgress)
            booking.MarkAsInProgress();
        else if (request.Status == BookingStatus.Delivered)
            booking.MarkAsDelivered();
        else if (request.Status == BookingStatus.Completed)
        {
            booking.MarkCompleted();
            
            // Set FinalFare to EstimatedFare if FinalFare is not already set
            // This ensures completed bookings always have a fare value for driver earnings
            if (!booking.FinalFare.HasValue && booking.EstimatedFare > 0)
            {
                booking.SetFinalFare(booking.EstimatedFare);
            }
            
            if (booking.SelectedDriverId.HasValue)
            {
                await _publishEndpoint.Publish(new BookingCompletedEvent
                {
                    BookingId = booking.Id,
                    DriverId = booking.SelectedDriverId.Value,
                    CustomerId = booking.CustomerId,
                    CompletedAt = DateTime.UtcNow,
                    FinalFare = booking.FinalFare ?? booking.EstimatedFare
                }, ct);
            }
        }
        else if (request.Status == BookingStatus.Cancelled)
        {
            // Legacy: Use CancelBookingCommand for proper cancellation with reason
            return Result.Fail<BookingDto>("Please use POST /api/bookings/{id}/cancel endpoint with cancellation reason");
        }

        await _repository.SaveChangesAsync(ct);

        if (previousStatus != booking.Status)
        {
            await PublishBookingStatusChangedAsync(booking, previousStatus);
        }

        // Send status update email if status actually changed
        if (previousStatus != booking.Status)
        {
            await SendStatusEmailAsync(booking, booking.Status, ct);
        }

        var dto = await BookingImageUrlResolver.ResolveAsync(BookingMapper.ToDto(booking), _fileStorage, ct);
        return Result.Ok(dto);
    }

    private async Task SendStatusEmailAsync(Booking booking, BookingStatus newStatus, CancellationToken ct)
    {
        try
        {
            // Get customer email
            var customer = await _customerRepository.GetByIdAsync(booking.CustomerId, ct);
            if (customer == null || string.IsNullOrEmpty(customer.Email))
                return;

            var customerEmail = customer.Email;
            var customerName = customer.Name ?? "Customer";

            switch (newStatus)
            {
                case BookingStatus.Dispatched:
                    await _bookingEmailService.SendBookingDispatchedAsync(
                        customerEmail,
                        customerName,
                        booking.BookingNumber,
                        booking.PickupLocation,
                        booking.DropoffLocation,
                        booking.ScheduleDate,
                        ct);
                    break;

                case BookingStatus.InProgress:
                    await _bookingEmailService.SendBookingInProgressAsync(
                        customerEmail,
                        customerName,
                        booking.BookingNumber,
                        booking.DropoffLocation,
                        ct);
                    break;

                case BookingStatus.Delivered:
                    await _bookingEmailService.SendBookingDeliveredAsync(
                        customerEmail,
                        customerName,
                        booking.BookingNumber,
                        booking.DropoffLocation,
                        DateTime.UtcNow,
                        ct);
                    break;

                case BookingStatus.Cancelled:
                    await _bookingEmailService.SendBookingCancelledAsync(
                        customerEmail,
                        customerName,
                        booking.BookingNumber,
                        booking.PickupLocation,
                        booking.DropoffLocation,
                        DateTime.UtcNow,
                        ct);
                    break;

                // OnTheWayToPickup, Completed, Pending - no email needed
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail the status update
            _logger.LogWarning(ex, "Failed to send status email for booking {BookingNumber}", booking.BookingNumber);
        }
    }

    private async Task PublishBookingStatusChangedAsync(Booking booking, BookingStatus previousStatus)
    {
        var payload = new
        {
            bookingId = booking.Id,
            bookingNumber = booking.BookingNumber,
            previousStatus = previousStatus.ToString(),
            currentStatus = booking.Status.ToString(),
            updatedAt = DateTime.UtcNow,
            completedStops = booking.Stops.Count(s => s.Status == StopStatus.Completed),
            totalStops = booking.Stops.Count
        };

        await BookingStatusNotifier.PublishToBookingPartiesAsync(
            _notificationService, _customerIdentityResolver, _chatPublisher, booking, "BookingStatusChanged", payload);
    }
}
