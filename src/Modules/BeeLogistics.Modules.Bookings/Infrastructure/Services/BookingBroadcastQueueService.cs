using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Contracts;
using Hangfire;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Bookings.Infrastructure.Services;

public class BookingBroadcastQueueService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BookingBroadcastQueueService> _logger;

    public BookingBroadcastQueueService(
        IServiceProvider serviceProvider,
        ILogger<BookingBroadcastQueueService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Per-booking one-shot job scheduled at a wave's expiry — the event/timer-driven advance that
    /// replaces tight pulsing. For a single booking it: expires offers past their deadline; if a driver
    /// is still actively considering a live offer it reschedules itself at that offer's expiry; otherwise,
    /// while still inside the search window, it requests the next wave (the consumer creates the next batch
    /// and schedules the following advance). Idempotent: safe to run alongside the recurring backstops.
    /// </summary>
    [AutomaticRetry(Attempts = 1)]
    public async Task AdvanceBroadcastAsync(Guid bookingId)
    {
        using var scope = _serviceProvider.CreateScope();
        var bookingRepository = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
        var offerRepository = scope.ServiceProvider.GetRequiredService<IDriverBookingOfferRepository>();
        var scheduler = scope.ServiceProvider.GetRequiredService<IBroadcastScheduler>();
        var bus = scope.ServiceProvider.GetRequiredService<IBus>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var booking = await bookingRepository.GetByIdAsync(bookingId, CancellationToken.None);
        if (booking == null)
            return;

        // Stop the chain once the booking is no longer actively searching.
        if (booking.SelectedDriverId.HasValue
            || booking.Status == BookingStatus.Cancelled
            || booking.Status == BookingStatus.Completed
            || booking.AssignmentStatus != BookingAssignmentStatus.BroadcastingToDrivers)
            return;

        var offers = await offerRepository.GetByBookingIdAsync(bookingId, CancellationToken.None);

        // Expire offers whose deadline passed so drivers stop seeing them and accept fails cleanly.
        var toExpire = offers.Where(o => o.Status == DriverOfferStatus.Pending && o.IsExpired).ToList();
        if (toExpire.Count > 0)
        {
            foreach (var offer in toExpire)
                offer.Expire();
            await offerRepository.SaveChangesAsync(CancellationToken.None);
            // The booking is already loaded here, so the mode tag is free.
            BeeLogistics.Shared.Infrastructure.BeeMetrics.OffersExpired.Add(
                toExpire.Count, ModeTag(booking.DeliveryMode));
        }

        // A driver still has a live offer — wait for them; re-check exactly at that offer's expiry.
        var live = offers.Where(o => o.Status == DriverOfferStatus.Pending && !o.IsExpired).ToList();
        if (live.Count > 0)
        {
            var nextDeadline = live.Min(o => o.ExpiresAt);
            scheduler.ScheduleAdvance(bookingId, nextDeadline);
            return;
        }

        // No live offers. Within the search window, ask for the next wave; the consumer's
        // "already broadcasting" path offers only drivers not yet contacted and schedules the next advance.
        // Read site 1 of 3 for MaxBroadcastMinutes — see the note on MaxBroadcastMinutesFor.
        var maxBroadcastMinutes = MaxBroadcastMinutesFor(booking, configuration);
        if (booking.CreatedAt > DateTime.UtcNow.AddMinutes(-maxBroadcastMinutes))
        {
            await bus.Publish(new BookingBroadcastRequested(bookingId), CancellationToken.None);
            _logger.LogDebug("[Advance] Requested next broadcast wave for booking {BookingId}", bookingId);
        }
        else
        {
            // Past the window: let the recurring backstops mark RejectedByAllDrivers. No reschedule.
            _logger.LogInformation("[Advance] Booking {BookingId} exceeded the {Minutes}-min broadcast window; leaving to backstops", bookingId, maxBroadcastMinutes);
        }
    }

    /// <summary>
    /// Hangfire recurring job (safety net, every 2 minutes): re-broadcast bookings still waiting for a driver
    /// and pick up drivers who came online after the initial broadcast. The real-time path is event-driven
    /// (push on offer creation + per-booking <see cref="AdvanceBroadcastAsync"/> + reject-triggered re-broadcast);
    /// this only catches lost timers, missed initial broadcasts, and newly available supply.
    /// Also picks up PendingAssignment bookings (e.g. when an initial outbox message was never consumed).
    /// </summary>
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 30, 60 })]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task PulseBroadcastingBookingsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var bookingRepository = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
        var offerRepository = scope.ServiceProvider.GetRequiredService<IDriverBookingOfferRepository>();
        var driverAvailabilityService = scope.ServiceProvider.GetRequiredService<IDriverAvailabilityService>();
        var offerNotifier = scope.ServiceProvider.GetRequiredService<IDriverOfferNotifier>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var maxPerTick = MaxBookingsPerPulseTick(configuration);

        // Due-only: NextPulseAt keeps this a small, indexed set instead of reprocessing
        // every broadcasting booking on every tick regardless of when it was last pulsed.
        var broadcastingBookings = await bookingRepository.GetDueForPulseAsync(
            DateTime.UtcNow, maxPerTick, CancellationToken.None);
        // Only consider PendingAssignment from the last 60 minutes so we don't hammer very old stale bookings
        var pendingToPulse = await bookingRepository.GetPendingAssignmentDueForDispatchAsync(
            DateTime.UtcNow.AddMinutes(-60), maxPerTick, CancellationToken.None);

        var maxOffers = configuration.GetValue<int>("BookingSettings:MaxOffersPerBroadcast", 15);
        var smallVehicleTypes = configuration.GetSection("BookingSettings:SmallVehicleTruckTypes").Get<string[]>()
            ?? new[] { "Closed Van", "L300" };

        // Re-sorted across both sources, not just within each. Each query is ordered and capped
        // separately, so concatenating them would interleave a low-rank broadcasting booking ahead
        // of a high-rank one still awaiting its first assignment — and the cap below would then
        // decide by source rather than by rank.
        var ordered = broadcastingBookings
            .Concat(pendingToPulse)
            .OrderByDescending(b => b.DispatchPriority)
            .ThenBy(b => b.NextPulseAt ?? DateTime.MinValue)
            .ThenBy(b => b.CreatedAt)
            .ToList();

        var toPulse = ordered.Take(maxPerTick).ToList();

        if (toPulse.Count == 0)
            return;

        // Never let the cap truncate silently: a tick that drops work while reporting success reads
        // as "everything was pulsed" in the logs, and the backlog only shows up as slow dispatch.
        if (ordered.Count > toPulse.Count)
            _logger.LogWarning(
                "[Pulse] {Total} booking(s) due but capped at {Cap} this tick; {Deferred} deferred to the next tick",
                ordered.Count, maxPerTick, ordered.Count - toPulse.Count);

        var pulsed = 0;
        var offersCreated = 0;

        foreach (var booking in toPulse)
        {
            try
            {
                // Skip if already accepted
                var offers = await offerRepository.GetByBookingIdAsync(booking.Id, CancellationToken.None);
                var hasAccepted = offers.Any(o => o.Status == DriverOfferStatus.Accepted);
                if (hasAccepted)
                    continue;

                // For PendingAssignment: change status to BroadcastingToDrivers first
                if (booking.AssignmentStatus == BookingAssignmentStatus.PendingAssignment)
                {
                    booking.StartBroadcastingToDrivers();
                    await bookingRepository.SaveChangesAsync(CancellationToken.None);
                    _logger.LogDebug("[Pulse] Changed booking {BookingId} from PendingAssignment to BroadcastingToDrivers", booking.Id);
                }

                // Find available drivers
                var availableDrivers = await driverAvailabilityService.GetAvailableDriversAsync(
                    booking.Id,
                    smallVehicleTypes,
                    CancellationToken.None);

                if (booking.AssignmentStatus == BookingAssignmentStatus.BroadcastingToDrivers && availableDrivers.Any())
                {
                    // Only create offers for newly available drivers (do not expire existing pending offers)
                    var existingOffers = await offerRepository.GetByBookingIdAsync(booking.Id, CancellationToken.None);
                    var excludedDriverIds = existingOffers.Select(o => o.DriverId).ToHashSet();
                    var driversToOffer = availableDrivers
                        .Where(d => !excludedDriverIds.Contains(d.DriverId))
                        .Take(maxOffers)
                        .ToList();

                    if (driversToOffer.Count > 0)
                    {
                        await CreateOffersForDriversAsync(booking, driversToOffer, offerRepository, offerNotifier, configuration, CancellationToken.None);
                        offersCreated += driversToOffer.Count;
                        _logger.LogDebug("[Pulse] Created {Count} new offer(s) for booking {BookingId} (newly available drivers only)", driversToOffer.Count, booking.Id);
                    }
                }

                if (booking.AssignmentStatus == BookingAssignmentStatus.BroadcastingToDrivers)
                    booking.SchedulePulse(ComputeNextPulseAt(
                        booking.CreatedAt, DateTime.UtcNow, configuration, booking.DeliveryMode));

                pulsed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Pulse] Error pulsing booking {BookingId}", booking.Id);
            }
        }

        if (pulsed > 0)
        {
            await bookingRepository.SaveChangesAsync(CancellationToken.None);
            _logger.LogInformation("[Pulse] Processed {Count} booking(s) (created {OffersCreated} offers).", pulsed, offersCreated);
        }
    }

    /// <summary>
    /// Per-booking pulse cadence with age-based backoff: fresh bookings are re-pulsed
    /// aggressively so newly-online drivers see them quickly, and stale ones back off so
    /// the due-only pulse query (GetDueForPulseAsync) stays cheap. Tiers default to the
    /// values documented in docs/BOOKING_PULSE_MECHANISM.md and are overridable via
    /// BookingSettings, per delivery mode under BookingSettings:DeliveryModes:{mode}. This only
    /// changes *when* a still-waiting booking is next looked at; the DB remains the source of
    /// truth, so the self-healing property is unaffected.
    /// </summary>
    public static DateTime ComputeNextPulseAt(
        DateTime createdAt, DateTime now, IConfiguration configuration, DeliveryMode mode = DeliveryMode.Regular)
    {
        var age = now - createdAt;
        var seconds =
            age < TimeSpan.FromMinutes(1) ? DeliveryModeSettings.Value(configuration, mode, "PulseIntervalFreshSeconds", 10)
            : age < TimeSpan.FromMinutes(5) ? DeliveryModeSettings.Value(configuration, mode, "PulseIntervalWarmSeconds", 30)
            : age < TimeSpan.FromMinutes(15) ? DeliveryModeSettings.Value(configuration, mode, "PulseIntervalCoolSeconds", 60)
            : DeliveryModeSettings.Value(configuration, mode, "PulseIntervalStaleSeconds", 120);
        return now.AddSeconds(seconds);
    }

    /// <summary>
    /// How long a booking of this mode is worth broadcasting for.
    /// </summary>
    /// <remarks>
    /// <b>This value is read in three places and all three must agree:</b> here via
    /// <see cref="AdvanceBroadcastAsync"/> (whether to request another wave),
    /// <see cref="ComputeGiveUpDeadline"/> (when to give up entirely), and
    /// <c>DriverAvailabilityService.ComputeSearchRings</c> (the denominator of the radius ramp).
    ///
    /// If one of them stays flat while the others go per-mode, a Pooling booking's window, its
    /// re-advance schedule and its radius growth disagree: it stops being advanced at the flat 10
    /// minutes while still claiming a 25-minute window, then sits there until a backstop tick
    /// happens to catch it. No exception and no log — just a booking that quietly stops trying.
    /// Hence this accessor, so the read is in one place per class rather than inline.
    /// </remarks>
    private static int MaxBroadcastMinutesFor(Booking booking, IConfiguration configuration)
        => DeliveryModeSettings.Value(configuration, booking.DeliveryMode, "MaxBroadcastMinutes", 10);

    /// <summary>
    /// Ceiling on how many bookings one pulse tick processes.
    /// </summary>
    /// <remarks>
    /// Without a cap, dispatch ordering is decorative: the tick iterated the whole due list, so
    /// ordering only rearranged work <i>within</i> a tick and never decided whether a high-rank
    /// booking got served at all. Set high (200) so it does not bind in normal operation — when it
    /// does bind, the tick logs it.
    /// </remarks>
    private static int MaxBookingsPerPulseTick(IConfiguration configuration)
        => configuration.GetValue("BookingSettings:MaxBookingsPerPulseTick", 200);

    /// <summary>The metric dimension that separates pooled offer churn from a real dispatch fault.</summary>
    private static KeyValuePair<string, object?> ModeTag(DeliveryMode mode)
        => new("mode", mode.ToString());

    private async Task CreateOffersForDriversAsync(
        Booking booking,
        IReadOnlyList<AvailableDriver> drivers,
        IDriverBookingOfferRepository offerRepository,
        IDriverOfferNotifier offerNotifier,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var maxOffers = configuration.GetValue("BookingSettings:MaxOffersPerBroadcast", 15);
        var expirationTime = OfferExpiry.Calculate(configuration, drivers.Count, maxOffers, booking.DeliveryMode);

        var newOffers = new List<DriverBookingOffer>(drivers.Count);
        foreach (var driver in drivers)
        {
            var offer = new DriverBookingOffer(
                booking.Id,
                driver.DriverId,
                driver.SequenceNumber,
                expirationTime,
                driver.DistanceKm
            );
            offerRepository.Add(offer);
            newOffers.Add(offer);
        }

        try
        {
            await offerRepository.SaveChangesAsync(ct);
            BeeLogistics.Shared.Infrastructure.BeeMetrics.OffersCreated.Add(drivers.Count);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
        {
            // Unique (BookingId, DriverId) index hit: a concurrent broadcast already
            // created one of these offers. Drop the batch — the next pulse recomputes
            // the exclusion list and creates only the genuinely missing offers.
            BeeLogistics.Shared.Infrastructure.BeeMetrics.OfferBatchConflicts.Add(1);
            _logger.LogWarning(ex,
                "Offer batch for booking {BookingId} conflicted with concurrently created offers; deferring to next pulse",
                booking.Id);
            return;
        }

        var pickup = booking.Stops.FirstOrDefault(s => s.Type == StopType.Pickup)?.Address;
        var dropoff = booking.Stops.FirstOrDefault(s => s.Type == StopType.Dropoff)?.Address;
        foreach (var offer in newOffers)
        {
            await offerNotifier.NotifyNewOfferAsync(
                offer.DriverId,
                new NewOfferNotification(
                    offer.Id, booking.Id, booking.BookingNumber,
                    pickup, dropoff, booking.EstimatedFare, offer.DistanceKm, offer.ExpiresAt),
                ct);
        }
    }

    /// <summary>
    /// Hangfire recurring job to process expired offers
    /// Runs every 10 seconds
    /// </summary>
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 10, 30, 60 })]
    [DisableConcurrentExecution(timeoutInSeconds: 30)]
    public async Task ProcessExpiredOffersAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var offerRepository = scope.ServiceProvider.GetRequiredService<IDriverBookingOfferRepository>();
        var bookingRepository = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        // Must come from the same scope as bookingRepository: the outbox stages onto whichever
        // BookingsDbContext this scope owns, and flushes when that context is saved.
        var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        var expiredOffers = await offerRepository.GetExpiredOffersAsync(CancellationToken.None);
        
        if (expiredOffers.Count == 0)
            return;

        // Batch process expired offers
        var bookingIdsToCheck = new HashSet<Guid>();
        
        foreach (var offer in expiredOffers)
        {
            try
            {
                offer.Expire();
                bookingIdsToCheck.Add(offer.BookingId);
                _logger.LogDebug("Expired offer {OfferId} for booking {BookingId}", offer.Id, offer.BookingId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing expired offer {OfferId}", offer.Id);
            }
        }

        // Save all changes at once
        if (expiredOffers.Count > 0)
        {
            await offerRepository.SaveChangesAsync(CancellationToken.None);

            // These offers span many bookings, so the modes come from one projected query rather
            // than from loading each aggregate. Counted per mode so a pooled expiry spike is
            // distinguishable from a general dispatch problem.
            var modes = await bookingRepository.GetDeliveryModesAsync(bookingIdsToCheck, CancellationToken.None);
            foreach (var byMode in expiredOffers.GroupBy(o => modes.GetValueOrDefault(o.BookingId)))
                BeeLogistics.Shared.Infrastructure.BeeMetrics.OffersExpired.Add(
                    byMode.Count(), ModeTag(byMode.Key));
        }

        // Check bookings that might need to be marked as rejected (batch)
        foreach (var bookingId in bookingIdsToCheck)
        {
            try
            {
                await TryMarkRejectedIfWindowExpiredAsync(bookingId, bookingRepository, offerRepository, publishEndpoint, configuration, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking booking {BookingId} after offer expiration", bookingId);
            }
        }

    }

    /// <summary>
    /// Gives up on a booking only once it has no live offers AND has been broadcasting
    /// longer than <c>BookingSettings:MaxBroadcastMinutes</c>. Marking a booking rejected
    /// the instant it has zero live offers is wrong for a booking that never had any offers
    /// in the first place (no drivers online yet) — that booking must stay in
    /// BroadcastingToDrivers so the pulse/advance retry chain keeps giving it a chance
    /// (see docs/BOOKING_PULSE_MECHANISM.md — "self-healing").
    /// </summary>
    private async Task TryMarkRejectedIfWindowExpiredAsync(
        Guid bookingId,
        IBookingRepository bookingRepository,
        IDriverBookingOfferRepository offerRepository,
        IPublishEndpoint publishEndpoint,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var allOffers = await offerRepository.GetByBookingIdAsync(bookingId, ct);
        var hasPendingOffers = allOffers.Any(o => o.Status == DriverOfferStatus.Pending && !o.IsExpired);
        if (hasPendingOffers)
            return;

        var booking = await bookingRepository.GetByIdAsync(bookingId, ct);
        if (booking == null || booking.AssignmentStatus != BookingAssignmentStatus.BroadcastingToDrivers)
            return;

        var deadline = ComputeGiveUpDeadline(
            booking.CreatedAt, booking.ScheduleDate, configuration, booking.DeliveryMode);
        if (DateTime.UtcNow < deadline)
            return; // still inside the retry window — leave it broadcasting

        booking.MarkAsRejectedByAllDrivers();

        // CancelledBy stays null: nobody chose to cancel this, the search simply ran out of road.
        // That is what tells the Payment module this is the platform's failure rather than the
        // customer's, and it is the strongest case for giving their money back (#51).
        // Published before the save so the outbox commits it with the cancellation.
        await publishEndpoint.Publish(new BookingCancelledEvent
        {
            BookingId = booking.Id,
            CustomerId = booking.CustomerId,
            CancelledAt = booking.CancelledAt ?? DateTime.UtcNow,
            CancellationReason = booking.CancellationReason,
            CancelledBy = booking.CancelledBy,
            SelectedDriverId = booking.SelectedDriverId
        }, ct);

        await bookingRepository.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Booking {BookingId} passed its give-up deadline {Deadline:o} with no accepted driver (scheduled {ScheduleDate:o})",
            bookingId, deadline, booking.ScheduleDate);
    }

    /// <summary>
    /// When a booking stops being worth broadcasting: the later of the on-demand retry window
    /// and a lead time before the scheduled slot.
    /// </summary>
    /// <remarks>
    /// Anchoring purely to <c>CreatedAt</c> only makes sense for on-demand work. A booking for
    /// next week was being abandoned ten minutes after it was created, with the whole week of
    /// opportunity still ahead of it — see #50.
    ///
    /// For an on-demand booking <c>ScheduleDate</c> is roughly now, so the second term is
    /// already in the past and this collapses to exactly the old rule. Scheduled bookings keep
    /// broadcasting until <c>ScheduledClaimLeadMinutes</c> before their slot, which leaves the
    /// customer time to make other arrangements rather than finding out at the slot itself.
    /// </remarks>
    public static DateTime ComputeGiveUpDeadline(
        DateTime createdAt, DateTime scheduleDate, IConfiguration configuration,
        DeliveryMode mode = DeliveryMode.Regular)
    {
        // Read site 2 of 3 for MaxBroadcastMinutes — see the note on MaxBroadcastMinutesFor.
        var maxBroadcastMinutes = DeliveryModeSettings.Value(configuration, mode, "MaxBroadcastMinutes", 10);
        var claimLeadMinutes = DeliveryModeSettings.Value(configuration, mode, "ScheduledClaimLeadMinutes", 45);

        // Same normalisation the create handler applies, so a stored Unspecified kind is not
        // shifted by the server's local offset.
        var scheduleUtc = scheduleDate.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(scheduleDate, DateTimeKind.Utc)
            : scheduleDate.ToUniversalTime();

        var onDemandDeadline = createdAt.AddMinutes(maxBroadcastMinutes);
        var scheduledDeadline = scheduleUtc.AddMinutes(-claimLeadMinutes);

        return onDemandDeadline > scheduledDeadline ? onDemandDeadline : scheduledDeadline;
    }

    /// <summary>
    /// Hangfire recurring job to process next driver in queue
    /// Runs every 10 seconds
    /// </summary>
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 10, 30, 60 })]
    [DisableConcurrentExecution(timeoutInSeconds: 30)]
    public async Task ProcessNextDriverInQueueAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var bookingRepository = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
        var offerRepository = scope.ServiceProvider.GetRequiredService<IDriverBookingOfferRepository>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        // Same scope as bookingRepository - see ProcessExpiredOffersAsync.
        var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        // Get bookings that are broadcasting, highest dispatch rank first and capped: this job also
        // used to iterate an unbounded, unordered list. ProcessExpiredOffersAsync is the backstop
        // for anything beyond the cap — it finds expired offers directly, without this query.
        var maxPerTick = MaxBookingsPerPulseTick(configuration);
        var broadcastingBookings = await bookingRepository.GetBroadcastingBookingsAsync(
            maxPerTick, CancellationToken.None);

        if (broadcastingBookings.Count == 0)
            return;

        if (broadcastingBookings.Count == maxPerTick)
            _logger.LogWarning(
                "[Queue] Broadcasting bookings hit the {Cap}-per-tick cap; lower-ranked bookings wait for the next tick",
                maxPerTick);

        var offersToExpire = new List<DriverBookingOffer>();
        var bookingsToCheck = new List<Guid>();

        foreach (var booking in broadcastingBookings)
        {
            try
            {
                // Get current active offer (pending, not expired)
                var currentOffer = await offerRepository.GetNextPendingOfferForBookingAsync(booking.Id, CancellationToken.None);

                if (currentOffer == null)
                {
                    // No more pending offers - check if all are rejected/expired
                    bookingsToCheck.Add(booking.Id);
                    continue;
                }

                // Check if current offer is expired (and not freshly created - grace period 30s)
                var offeredAgo = DateTime.UtcNow - currentOffer.OfferedAt;
                if (currentOffer.IsExpired && offeredAgo > TimeSpan.FromSeconds(30))
                {
                    offersToExpire.Add(currentOffer);
                    bookingsToCheck.Add(booking.Id);
                    _logger.LogDebug("Current offer {OfferId} expired, will move to next in queue", currentOffer.Id);
                }
                // If offer is still pending and not expired, it's active - do nothing
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing booking {BookingId} queue", booking.Id);
            }
        }

        // Batch expire offers
        if (offersToExpire.Count > 0)
        {
            foreach (var offer in offersToExpire)
            {
                offer.Expire();
            }
            await offerRepository.SaveChangesAsync(CancellationToken.None);
        }

        // Batch check bookings
        foreach (var bookingId in bookingsToCheck)
        {
            try
            {
                await TryMarkRejectedIfWindowExpiredAsync(bookingId, bookingRepository, offerRepository, publishEndpoint, configuration, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking booking {BookingId}", bookingId);
            }
        }
    }
}
