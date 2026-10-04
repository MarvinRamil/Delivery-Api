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

public record CancelBookingCommand(Guid Id, string Reason, Guid CancelledByUserId, bool IsElevated = false) : IRequest<Result<BookingDto>>;

public class CancelBookingCommandHandler : IRequestHandler<CancelBookingCommand, Result<BookingDto>>
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
    private readonly ILogger<CancelBookingCommandHandler> _logger;

    public CancelBookingCommandHandler(
        IBookingRepository repository,
        ICustomerRepository customerRepository,
        IBookingEmailService bookingEmailService,
        IPublishEndpoint publishEndpoint,
        INotificationService notificationService,
        ICustomerIdentityResolver customerIdentityResolver,
        IDirectBusPublisher chatPublisher,
        IFileStorageService fileStorage,
        IBookingAccessPolicy accessPolicy,
        ILogger<CancelBookingCommandHandler> logger)
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

    public async Task<Result<BookingDto>> Handle(CancelBookingCommand request, CancellationToken ct)
    {
        var booking = await _repository.GetByIdAsync(request.Id, ct);
        if (booking == null)
            return Result.NotFound<BookingDto>("Booking not found");

        // Owning customer, assigned driver, or backoffice. The elevated arm is new: backoffice
        // previously could not cancel a booking at all.
        if (!await _accessPolicy.CanAccessAsync(booking, request.CancelledByUserId, request.IsElevated, ct))
            return Result.Forbidden<BookingDto>("You can only cancel your own bookings or bookings assigned to you");

        // Validate booking can be cancelled
        if (booking.Status == BookingStatus.Completed)
            return Result.Fail<BookingDto>("Cannot cancel a completed booking");

        if (booking.Status == BookingStatus.Cancelled)
            return Result.Fail<BookingDto>("Booking is already cancelled");

        // Validate reason is provided
        if (string.IsNullOrWhiteSpace(request.Reason))
            return Result.Fail<BookingDto>("Cancellation reason is required");

        var previousStatus = booking.Status;

        // Cancel the booking
        try
        {
            booking.Cancel(request.Reason, request.CancelledByUserId);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Fail<BookingDto>(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result.Fail<BookingDto>(ex.Message);
        }

        // Published before the save so the MassTransit EF outbox stages it in the same transaction
        // as the cancellation itself - a booking can never end up Cancelled with no event emitted.
        // Same ordering as BookingCompletedEvent in CompleteStop.
        await _publishEndpoint.Publish(new BookingCancelledEvent
        {
            BookingId = booking.Id,
            CustomerId = booking.CustomerId,
            CancelledAt = booking.CancelledAt ?? DateTime.UtcNow,
            CancellationReason = booking.CancellationReason,
            CancelledBy = booking.CancelledBy,
            SelectedDriverId = booking.SelectedDriverId
        }, ct);

        await _repository.SaveChangesAsync(ct);

        // Publish status change event
        if (previousStatus != booking.Status)
        {
            await PublishBookingStatusChangedAsync(booking, previousStatus, ct);
        }

        // Send cancellation email
        await SendCancellationEmailAsync(booking, request.Reason, ct);

        var dto = await BookingImageUrlResolver.ResolveAsync(BookingMapper.ToDto(booking), _fileStorage, ct);
        return Result.Ok(dto);
    }

    private async Task PublishBookingStatusChangedAsync(Booking booking, BookingStatus previousStatus, CancellationToken ct)
    {
        var payload = new
        {
            bookingId = booking.Id,
            bookingNumber = booking.BookingNumber,
            previousStatus = previousStatus.ToString(),
            currentStatus = booking.Status.ToString(),
            cancelledBy = booking.CancelledBy,
            cancellationReason = booking.CancellationReason
        };

        // cancellationReason can carry customer-typed free text ("Other: {customReason}"),
        // so this goes to the two parties only — never to every connected client.
        await BookingStatusNotifier.PublishToBookingPartiesAsync(
            _notificationService, _customerIdentityResolver, _chatPublisher, booking, "BookingStatusChanged", payload, ct: ct);
    }

    private async Task SendCancellationEmailAsync(Booking booking, string reason, CancellationToken ct)
    {
        try
        {
            var customer = await _customerRepository.GetByIdAsync(booking.CustomerId, ct);
            if (customer == null || string.IsNullOrEmpty(customer.Email))
                return;

            await _bookingEmailService.SendBookingCancelledAsync(
                customer.Email,
                customer.Name ?? "Customer",
                booking.BookingNumber,
                booking.PickupLocation,
                booking.DropoffLocation,
                DateTime.UtcNow,
                ct);
        }
        catch (Exception ex)
        {
            // Log but don't fail the cancellation
            _logger.LogWarning(ex, "Failed to send cancellation email for booking {BookingNumber}", booking.BookingNumber);
        }
    }
}
