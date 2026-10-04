using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Bookings.Application.Consumers;

public class BookingBroadcastConsumer : IConsumer<BookingBroadcastRequested>
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IDriverBookingOfferRepository _offerRepository;
    private readonly IDriverAvailabilityService _driverAvailabilityService;
    private readonly IDriverOfferNotifier _offerNotifier;
    private readonly IBroadcastScheduler _scheduler;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BookingBroadcastConsumer> _logger;

    public BookingBroadcastConsumer(
        IBookingRepository bookingRepository,
        IDriverBookingOfferRepository offerRepository,
        IDriverAvailabilityService driverAvailabilityService,
        IDriverOfferNotifier offerNotifier,
        IBroadcastScheduler scheduler,
        IConfiguration configuration,
        ILogger<BookingBroadcastConsumer> logger)
    {
        _bookingRepository = bookingRepository;
        _offerRepository = offerRepository;
        _driverAvailabilityService = driverAvailabilityService;
        _offerNotifier = offerNotifier;
        _scheduler = scheduler;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<BookingBroadcastRequested> context)
    {
        var bookingId = context.Message.BookingId;
        _logger.LogInformation("[BookingBroadcast] Consumer received BookingBroadcastRequested for booking {BookingId}. Processing...", bookingId);

        var booking = await _bookingRepository.GetByIdAsync(bookingId, context.CancellationToken);
        if (booking == null)
        {
            _logger.LogWarning("[BookingBroadcast] Booking {BookingId} not found in DB. Skipping broadcast.", bookingId);
            return;
        }

        var maxOffers = _configuration.GetValue<int>("BookingSettings:MaxOffersPerBroadcast", 15);
        var smallVehicleTypes = _configuration.GetSection("BookingSettings:SmallVehicleTruckTypes").Get<string[]>()
            ?? new[] { "Closed Van", "L300" };

        // Already broadcasting = pulse: refresh offers so drivers see the job again before expiry
        if (booking.AssignmentStatus == BookingAssignmentStatus.BroadcastingToDrivers)
        {
            await PulseOffersAsync(booking, maxOffers, smallVehicleTypes, context.CancellationToken);
        }
        else
        {
            // First-time broadcast: set state even with 0 drivers so we can advance/retry later
            booking.StartBroadcastingToDrivers();
            await _bookingRepository.SaveChangesAsync(context.CancellationToken);

            _logger.LogInformation("[BookingBroadcast] Finding available drivers for booking {BookingId}...", bookingId);
            var availableDrivers = await _driverAvailabilityService.GetAvailableDriversAsync(
                bookingId,
                smallVehicleTypes,
                context.CancellationToken);

            if (availableDrivers.Any())
            {
                // Cap: create offers only for the closest N drivers (avoids 1000 drivers = 1000 offers)
                var driversToOffer = availableDrivers.Take(maxOffers).ToList();
                await CreateOffersForDriversAsync(booking, driversToOffer, context.CancellationToken);
                _logger.LogInformation("[BookingBroadcast] Broadcast started for booking {BookingId} with {DriverCount} driver offer(s) (max {MaxOffers} per broadcast).", bookingId, driversToOffer.Count, maxOffers);
            }
            else
            {
                _logger.LogInformation("[BookingBroadcast] No drivers found for booking {BookingId}. Booking is now broadcasting; the scheduled advance will retry for newly available drivers.", bookingId);
            }
        }

        // Schedule the per-booking deadline advance (timer-driven progression — no population-wide pulse).
        await ScheduleNextAdvanceAsync(booking, context.CancellationToken);
    }

    /// <summary>
    /// Arms the next one-shot advance for this booking: at the current wave's earliest expiry if offers
    /// are live, otherwise after a short retry window so a no-driver booking is re-attempted.
    /// </summary>
    private async Task ScheduleNextAdvanceAsync(Booking booking, CancellationToken ct)
    {
        // Don't schedule if the booking already moved on (e.g. accepted concurrently).
        if (booking.SelectedDriverId.HasValue
            || booking.Status == BookingStatus.Cancelled
            || booking.Status == BookingStatus.Completed)
            return;

        var offers = await _offerRepository.GetByBookingIdAsync(booking.Id, ct);
        var liveExpiries = offers
            .Where(o => o.Status == DriverOfferStatus.Pending && !o.IsExpired)
            .Select(o => o.ExpiresAt)
            .ToList();

        DateTime deadline;
        if (liveExpiries.Count > 0)
        {
            deadline = liveExpiries.Min();
        }
        else
        {
            // No live offers (e.g. no drivers available yet) — retry within the search window.
            // Deliberately separate from OfferExpirationFewDriversMinutes (offer TTL): this is the
            // re-check cadence, which must be short enough to give a few real attempts inside
            // BookingSettings:MaxBroadcastMinutes before the booking gives up.
            var retryMinutes = DeliveryModeSettings.Value(
                _configuration, booking.DeliveryMode, "NoDriverRetryMinutes", 5);
            deadline = DateTime.UtcNow.AddMinutes(retryMinutes);
        }

        _scheduler.ScheduleAdvance(booking.Id, deadline);
    }

    /// <summary>
    /// Pulse: create offers only for newly available drivers (do not expire existing pending offers).
    /// </summary>
    private async Task PulseOffersAsync(
        Booking booking,
        int maxOffers,
        IReadOnlyList<string> smallVehicleTypes,
        CancellationToken ct)
    {
        var bookingId = booking.Id;

        var availableDrivers = await _driverAvailabilityService.GetAvailableDriversAsync(
            bookingId,
            smallVehicleTypes,
            ct);

        if (!availableDrivers.Any())
        {
            _logger.LogDebug("[BookingBroadcast] Pulse for booking {BookingId}: no available drivers this time.", bookingId);
            return;
        }

        // Only offer to drivers who don't already have an offer (any status) for this booking
        var existingOffers = await _offerRepository.GetByBookingIdAsync(bookingId, ct);
        var excludedDriverIds = existingOffers.Select(o => o.DriverId).ToHashSet();
        var driversToOffer = availableDrivers
            .Where(d => !excludedDriverIds.Contains(d.DriverId))
            .Take(maxOffers)
            .ToList();

        if (driversToOffer.Count == 0)
        {
            _logger.LogDebug("[BookingBroadcast] Pulse for booking {BookingId}: no newly available drivers (all already offered).", bookingId);
            return;
        }

        await CreateOffersForDriversAsync(booking, driversToOffer, ct);
        _logger.LogInformation("[BookingBroadcast] Pulsed booking {BookingId} with {DriverCount} new offer(s) (newly available drivers only).", bookingId, driversToOffer.Count);
    }

    private async Task CreateOffersForDriversAsync(
        Booking booking,
        IReadOnlyList<AvailableDriver> drivers,
        CancellationToken ct)
    {
        var maxOffers = _configuration.GetValue("BookingSettings:MaxOffersPerBroadcast", 15);
        var expirationTime = OfferExpiry.Calculate(_configuration, drivers.Count, maxOffers, booking.DeliveryMode);

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
            _offerRepository.Add(offer);
            newOffers.Add(offer);
        }

        try
        {
            await _offerRepository.SaveChangesAsync(ct);
            BeeLogistics.Shared.Infrastructure.BeeMetrics.OffersCreated.Add(drivers.Count);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
        {
            BeeLogistics.Shared.Infrastructure.BeeMetrics.OfferBatchConflicts.Add(1);
            // Unique (BookingId, DriverId) index hit: another broadcast created one of
            // these offers concurrently. Safe to drop — the safety-net pulse recomputes
            // missing offers from the exclusion list on its next run.
            _logger.LogWarning(ex,
                "Offer batch for booking {BookingId} conflicted with concurrently created offers; deferring to pulse",
                booking.Id);
            return;
        }

        // Push each new offer to its driver (SignalR + push) so they see it immediately
        // instead of waiting for the next poll. Best-effort; never aborts the broadcast.
        await NotifyDriversAsync(booking, newOffers, ct);
    }

    private async Task NotifyDriversAsync(Booking booking, IReadOnlyList<DriverBookingOffer> offers, CancellationToken ct)
    {
        var pickup = booking.Stops.FirstOrDefault(s => s.Type == StopType.Pickup)?.Address;
        var dropoff = booking.Stops.FirstOrDefault(s => s.Type == StopType.Dropoff)?.Address;

        foreach (var offer in offers)
        {
            await _offerNotifier.NotifyNewOfferAsync(
                offer.DriverId,
                new NewOfferNotification(
                    offer.Id,
                    booking.Id,
                    booking.BookingNumber,
                    pickup,
                    dropoff,
                    booking.EstimatedFare,
                    offer.DistanceKm,
                    offer.ExpiresAt),
                ct);
        }
    }
}
