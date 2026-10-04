using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Infrastructure;
using BeeLogistics.Shared.Hubs;
using BeeLogistics.Modules.Identity.Domain;
using MassTransit;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

// Commands
public record AcceptBookingOfferCommand(Guid OfferId, Guid DriverId) : IRequest<Result<DriverBookingOfferDto>>;

public class AcceptBookingOfferCommandHandler : IRequestHandler<AcceptBookingOfferCommand, Result<DriverBookingOfferDto>>
{
    private readonly IDriverBookingOfferRepository _offerRepository;
    private readonly IBookingRepository _bookingRepository;
    private readonly INotificationService _notificationService;
    private readonly ICustomerIdentityResolver _customerIdentityResolver;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<AcceptBookingOfferCommandHandler> _logger;

    public AcceptBookingOfferCommandHandler(
        IDriverBookingOfferRepository offerRepository,
        IBookingRepository bookingRepository,
        INotificationService notificationService,
        ICustomerIdentityResolver customerIdentityResolver,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        IPublishEndpoint publishEndpoint,
        ILogger<AcceptBookingOfferCommandHandler> logger)
    {
        _offerRepository = offerRepository;
        _bookingRepository = bookingRepository;
        _notificationService = notificationService;
        _customerIdentityResolver = customerIdentityResolver;
        _userManager = userManager;
        _configuration = configuration;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task<Result<DriverBookingOfferDto>> Handle(AcceptBookingOfferCommand request, CancellationToken ct)
    {
        // SECURITY: Only active, approved (onboarded) drivers may accept bookings.
        // Client-side gating alone is not sufficient — enforce server-side.
        var driverUser = await _userManager.FindByIdAsync(request.DriverId.ToString());
        if (driverUser == null || !driverUser.IsActive || !driverUser.IsOnboarded)
            return Result.Fail<DriverBookingOfferDto>("Driver is not approved to accept bookings");

        var offer = await _offerRepository.GetByIdAsync(request.OfferId, ct);
        if (offer == null)
            return Result.NotFound<DriverBookingOfferDto>("Offer not found");

        if (offer.DriverId != request.DriverId)
            return Result.Fail<DriverBookingOfferDto>("Offer does not belong to this driver");

        if (offer.Status != DriverOfferStatus.Pending)
            return Result.Fail<DriverBookingOfferDto>("Offer is not pending");

        if (offer.IsExpired)
            return Result.Fail<DriverBookingOfferDto>("Offer has expired");

        // Check if another driver already accepted (race condition check)
        var booking = await _bookingRepository.GetByIdAsync(offer.BookingId, ct);
        if (booking == null)
            return Result.Fail<DriverBookingOfferDto>("Booking not found");

        if (booking.AssignmentStatus == BookingAssignmentStatus.AcceptedByDriver)
        {
            // Check if this offer was the one accepted
            var acceptedOffer = await _offerRepository
                .FindAsync(o => o.BookingId == offer.BookingId && o.Status == DriverOfferStatus.Accepted, ct);
            if (acceptedOffer.Any() && acceptedOffer.First().Id != offer.Id)
                return Result.Fail<DriverBookingOfferDto>("Another driver has already accepted this booking");
        }

        // CAPACITY: a driver may only hold so many bookings at once (#55). Opened before the
        // count and held past the save, so a driver's two simultaneous accepts cannot both read
        // the same under-limit count and both pass.
        var limits = DriverCapacityPolicy.ReadLimits(_configuration);
        var imminentThreshold = DriverCapacityPolicy.ImminentThreshold(DateTime.UtcNow, limits);
        var claimsFutureSlot = DriverCapacityPolicy.IsScheduledAhead(booking.ScheduleDate, imminentThreshold);

        await using var capacityScope = await _bookingRepository.BeginDriverCapacityScopeAsync(request.DriverId, ct);

        var workload = await _bookingRepository.GetDriverWorkloadAsync(request.DriverId, imminentThreshold, ct);
        var capacityRejection = DriverCapacityPolicy.RejectionReason(workload, limits, claimsFutureSlot);
        if (capacityRejection != null)
        {
            _logger.LogInformation(
                "Driver {DriverId} refused booking {BookingId}: at capacity (active={Active}/{MaxActive}, scheduled={Scheduled}/{MaxScheduled}, booking is {Kind})",
                request.DriverId, booking.Id, workload.Active, limits.MaxActive,
                workload.ScheduledAhead, limits.MaxScheduledAhead,
                claimsFutureSlot ? "a future claim" : "active work");
            return Result.Fail<DriverBookingOfferDto>(capacityRejection);
        }

        // Accept the offer
        offer.Accept();

        // Update booking
        booking.AcceptByDriver(offer.DriverId);

        // Cancel all other pending offers for this booking (excluding the one we just accepted)
        await _offerRepository.CancelAllPendingOffersForBookingAsync(offer.BookingId, excludeOfferId: offer.Id, ct);

        // Note: Dispatch creation will be handled separately
        // For now, just accept the offer and update booking
        // The system can create dispatch later based on user role (Driver vs Owner)

        try
        {
            await _offerRepository.SaveChangesAsync(ct);

            // Seat the driver in the booking's chat room (#78). Published BEFORE the booking save,
            // not after: the outbox stages onto BookingsDbContext and flushes when it is saved, so
            // a publish after the last save is staged and then silently discarded when the scope
            // disposes - the same failure that lost a wallet reversal in #27. Staging it here also
            // means the concurrency rollback below discards it, which is right: if another driver
            // won the race, this driver must not be added to the room.
            await _publishEndpoint.Publish(new BookingChatDriverAssigned
            {
                BookingId = booking.Id,
                DriverId = offer.DriverId,
                DriverName = driverUser?.FullName,
                AssignedAt = DateTime.UtcNow,
            }, ct);

            await _bookingRepository.SaveChangesAsync(ct);
            await capacityScope.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The booking's xmin token changed between our read and this save:
            // another driver claimed the booking first. The status check above
            // cannot catch a truly simultaneous accept; this can.
            // Leaving the scope uncommitted rolls the accept back with it.
            BeeMetrics.AcceptConflicts.Add(1);
            return Result.Fail<DriverBookingOfferDto>("Another driver has already accepted this booking");
        }

        BeeMetrics.OffersAccepted.Add(1);

        // Publish status change event to notify customer (so they can start tracking)
        var payload = new
        {
            bookingId = booking.Id,
            bookingNumber = booking.BookingNumber,
            previousStatus = BookingStatus.Pending.ToString(),
            currentStatus = booking.Status.ToString(),
            updatedAt = DateTime.UtcNow,
            selectedDriverId = booking.SelectedDriverId,
            completedStops = booking.Stops.Count(s => s.Status == StopStatus.Completed),
            totalStops = booking.Stops.Count
        };

        // Customer only: the driver just accepted, so they already know. Deliberately narrower
        // than the other BookingStatusChanged sites.
        await BookingStatusNotifier.PublishToBookingPartiesAsync(
            // null: the room is already told by BookingChatDriverAssigned above, and this status
            // change IS the driver assignment - posting both would say the same thing twice.
            _notificationService, _customerIdentityResolver, null, booking, "BookingStatusChanged", payload,
            includeDriver: false, ct: ct);

        return Result.Ok(DriverOfferMapper.ToDto(offer));
    }
}
