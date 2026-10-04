using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Map.Application.Interfaces;
using BeeLogistics.Modules.Map.Application.Services;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Bookings.Infrastructure.Services;

/// <summary>
/// Service for determining driver availability for bookings.
/// Candidate selection uses the H3 geo index (Redis) when the booking has pickup
/// coordinates: nearest drivers are found via expanding hex rings, then verified
/// against Identity (active + Driver role). Falls back to a full driver scan when
/// the index yields no candidates (Redis down, no drivers reporting locations, or
/// booking without pickup coordinates).
/// Note: in the independent-driver model, drivers use their own vehicles (no fleet-owned trucks)
/// </summary>
public class DriverAvailabilityService : IDriverAvailabilityService
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IDriverBookingOfferRepository _offerRepository;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IMediator _mediator;
    private readonly IDistanceCalculationService _distanceService;
    private readonly IDriverGeoIndex _geoIndex;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DriverAvailabilityService> _logger;

    public DriverAvailabilityService(
        IBookingRepository bookingRepository,
        IDriverBookingOfferRepository offerRepository,
        UserManager<ApplicationUser> userManager,
        IMediator mediator,
        IDistanceCalculationService distanceService,
        IDriverGeoIndex geoIndex,
        IConfiguration configuration,
        ILogger<DriverAvailabilityService> logger)
    {
        _bookingRepository = bookingRepository;
        _offerRepository = offerRepository;
        _userManager = userManager;
        _mediator = mediator;
        _distanceService = distanceService;
        _geoIndex = geoIndex;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AvailableDriver>> GetAvailableDriversAsync(
        Guid bookingId,
        IReadOnlyList<string> smallVehicleTypes,
        CancellationToken ct = default)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId, ct);

        if (booking == null)
        {
            _logger.LogWarning("Booking {BookingId} not found when checking driver availability", bookingId);
            return Array.Empty<AvailableDriver>();
        }

        var proximityRadiusKm = _configuration.GetValue<decimal>("BookingSettings:ProximityRadiusKm", 0);

        if (booking.PickupLatitude.HasValue && booking.PickupLongitude.HasValue)
        {
            var maxRings = ComputeSearchRings(booking.CreatedAt, booking.DeliveryMode);
            var fromIndex = await GetFromGeoIndexAsync(
                booking,
                proximityRadiusKm,
                maxRings,
                ct);

            if (fromIndex.Count > 0)
            {
                BeeLogistics.Shared.Infrastructure.BeeMetrics.MatchingGeoQueries.Add(1,
                    new KeyValuePair<string, object?>("result", "hit"));
                BeeLogistics.Shared.Infrastructure.BeeMetrics.MatchingCandidates.Record(fromIndex.Count);
                return fromIndex;
            }

            BeeLogistics.Shared.Infrastructure.BeeMetrics.MatchingGeoQueries.Add(1,
                new KeyValuePair<string, object?>("result", "empty"));
            _logger.LogInformation(
                "H3 geo index returned no candidates for booking {BookingId}; falling back to full driver scan",
                bookingId);
        }

        BeeLogistics.Shared.Infrastructure.BeeMetrics.MatchingFallbackScans.Add(1);
        var fromScan = await GetByFullScanAsync(booking, bookingId, proximityRadiusKm, ct);
        BeeLogistics.Shared.Infrastructure.BeeMetrics.MatchingCandidates.Record(fromScan.Count);
        return fromScan;
    }

    /// <summary>
    /// Widens the H3 ring search as a booking ages, from
    /// BookingSettings:H3InitialSearchRadiusKm up to H3MaxSearchRadiusKm, reaching
    /// the max radius right as BookingSettings:MaxBroadcastMinutes (the give-up
    /// window) elapses — so a booking in a low-density area casts a wider net the
    /// longer it searches instead of retrying the same fixed radius until it gives up.
    /// Uses the ~1km-per-ring approximation H3DriverGeoIndex is already sized with
    /// (res 8, k=3 ≈ 3km).
    ///
    /// All three values are per-mode overridable. A mode that searches "aggressively from t=0"
    /// is not a separate code path — it is simply a larger H3InitialSearchRadiusKm.
    ///
    /// MaxBroadcastMinutes is read here as well as twice in BookingBroadcastQueueService, and the
    /// three must agree: this is the denominator of the ramp, so a booking whose give-up window was
    /// widened without widening it here would reach its maximum radius long before it stops
    /// searching, and spend the rest of the window re-querying the same widest ring.
    /// </summary>
    private int ComputeSearchRings(DateTime bookingCreatedAt, Domain.DeliveryMode mode)
    {
        var maxBroadcastMinutes = DeliveryModeSettings.Value(_configuration, mode, "MaxBroadcastMinutes", 10);
        var initialRadiusKm = DeliveryModeSettings.Value(_configuration, mode, "H3InitialSearchRadiusKm", 3m);
        var maxRadiusKm = DeliveryModeSettings.Value(_configuration, mode, "H3MaxSearchRadiusKm", 10m);

        var elapsedMinutes = (DateTime.UtcNow - bookingCreatedAt).TotalMinutes;
        var progress = maxBroadcastMinutes > 0
            ? Math.Clamp(elapsedMinutes / maxBroadcastMinutes, 0, 1)
            : 1;
        var radiusKm = initialRadiusKm + (maxRadiusKm - initialRadiusKm) * (decimal)progress;
        // Floor (not Ceiling): keeps a fresh booking exactly at the initial radius
        // instead of jumping a ring the instant any time at all has elapsed, while
        // still landing exactly on the max radius once the window fully elapses.
        return (int)Math.Floor(radiusKm);
    }

    /// <summary>
    /// A driver in the running, before ranking: who they are, how far from pickup, and where they
    /// are now. The last two are separate because they come from different places — distance is
    /// computed to pickup, position is the driver's own last known location, and either can be
    /// absent independently.
    /// </summary>
    private readonly record struct RankCandidate(
        Guid DriverId,
        decimal? DistanceKm,
        decimal? Latitude,
        decimal? Longitude);

    /// <summary>
    /// The one ranking: highest score first, closest as the tiebreak, drivers with no location last,
    /// then numbered from 1.
    /// </summary>
    /// <remarks>
    /// Both candidate paths — the H3 index and the fallback scan — used to rank with separately
    /// written but identical code. That is exactly where a scoring change drifts: one path picking
    /// up a bias while the other quietly does not looks like a flaky market rather than a bug, since
    /// which path runs depends on whether Redis had candidates. Extracted so there is one place to
    /// change and one place to test.
    ///
    /// <paramref name="bonusFor"/> defaults to no bias, which yields byte-identical scores and order
    /// to the pre-bias behaviour — see <see cref="AvailableDriver.RankingScore"/>.
    /// </remarks>
    private static List<AvailableDriver> Rank(
        IEnumerable<RankCandidate> candidates,
        Func<RankCandidate, double>? bonusFor = null)
        => candidates
            .Select(c => new
            {
                Candidate = c,
                Score = AvailableDriver.RankingScore(c.DistanceKm, bonusFor?.Invoke(c) ?? 0.0),
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Candidate.DistanceKm ?? decimal.MaxValue)
            .Select((x, index) => new AvailableDriver(
                x.Candidate.DriverId,
                x.Candidate.DistanceKm,
                index + 1,
                x.Score))
            .ToList();

    /// <summary>
    /// The mode-specific ranking bias for this booking, or <c>null</c> for no bias at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Returns null unless a weight is configured above zero</b>, and that early exit is the
    /// point: with the weights unset — which is how this ships — not one extra query is issued and
    /// <see cref="Rank"/> produces byte-identical scores to before the bias existed. "Inert" here
    /// means genuinely inert, not "computes a zero".
    /// </para>
    /// <para>
    /// At most one extra query per candidate selection, never one per driver. Both branches take
    /// the whole candidate set and fetch in a single call.
    /// </para>
    /// </remarks>
    private async Task<Func<RankCandidate, double>?> BuildBonusAsync(
        Domain.Booking booking,
        IReadOnlyList<RankCandidate> candidates,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
            return null;

        return booking.DeliveryMode switch
        {
            Domain.DeliveryMode.Pooling => await BuildPoolingBonusAsync(booking, candidates, ct),
            Domain.DeliveryMode.OnDemand => await BuildResponsivenessBonusAsync(booking, candidates, ct),
            _ => null,
        };
    }

    /// <summary>
    /// Prefers drivers whose current work already takes them past this booking. See
    /// <see cref="PoolingCompatibility"/> for why this is a detour score and not a distance.
    /// </summary>
    private async Task<Func<RankCandidate, double>?> BuildPoolingBonusAsync(
        Domain.Booking booking,
        IReadOnlyList<RankCandidate> candidates,
        CancellationToken ct)
    {
        var weight = DeliveryModeSettings.Value(_configuration, Domain.DeliveryMode.Pooling, "PoolingScoreWeight", 0.0);
        if (weight <= 0)
            return null;

        var pickup = PickupPoint(booking);
        var dropoff = FinalDropoffPoint(booking);
        if (pickup is null || dropoff is null)
            return null;

        var maxDetourKm = DeliveryModeSettings.Value(_configuration, Domain.DeliveryMode.Pooling, "MaxDetourKm", 4.0);
        var candidateLimit = DeliveryModeSettings.Value(_configuration, Domain.DeliveryMode.Pooling, "PoolingCandidateLimit", 45);

        // Score only the closest N. Detour scoring is O(candidates x held legs) trigonometry on a
        // 15-second tick, and a driver ranked 60th on proximity is not going to be offered this
        // booking however well it fits their route.
        var scored = candidates
            .Where(c => c.Latitude.HasValue && c.Longitude.HasValue)
            .OrderByDescending(c => AvailableDriver.ProximityScore(c.DistanceKm))
            .Take(candidateLimit)
            .ToList();

        if (scored.Count == 0)
            return null;

        var heldLegs = await _bookingRepository.GetHeldLegsForDriversAsync(
            scored.Select(c => c.DriverId).ToList(), ct);

        if (heldLegs.Count == 0)
            return null;

        var legsByDriver = heldLegs
            .GroupBy(l => l.DriverId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<GeoPoint>)g.Select(l => new GeoPoint(l.Latitude, l.Longitude)).ToList());

        // Computed up front rather than inside the ranking callback: the trigonometry then runs
        // exactly once per candidate no matter how the sort enumerates, and the metric below is a
        // count rather than something incremented from inside a comparer.
        var bonuses = new Dictionary<Guid, double>(scored.Count);
        foreach (var candidate in scored)
        {
            if (!legsByDriver.TryGetValue(candidate.DriverId, out var legs))
                continue;

            var score = PoolingCompatibility.BestScore(
                new GeoPoint(candidate.Latitude!.Value, candidate.Longitude!.Value),
                pickup.Value,
                dropoff.Value,
                legs,
                maxDetourKm);

            if (score > 0)
                bonuses[candidate.DriverId] = weight * score;
        }

        if (bonuses.Count == 0)
            return null;

        BeeLogistics.Shared.Infrastructure.BeeMetrics.PoolingBiasApplied.Add(bonuses.Count);
        _logger.LogDebug(
            "[Pooling] Booking {BookingId}: {Boosted} of {Scored} candidate(s) are on a compatible route",
            booking.Id, bonuses.Count, scored.Count);

        return candidate => bonuses.GetValueOrDefault(candidate.DriverId);
    }

    /// <summary>
    /// Prefers drivers who actually take the offers they are sent. See
    /// <see cref="DriverQualityScore"/> for why this is acceptance rate and not star rating.
    /// </summary>
    private async Task<Func<RankCandidate, double>?> BuildResponsivenessBonusAsync(
        Domain.Booking booking,
        IReadOnlyList<RankCandidate> candidates,
        CancellationToken ct)
    {
        var weight = DeliveryModeSettings.Value(_configuration, booking.DeliveryMode, "QualityScoreWeight", 0.0);
        if (weight <= 0)
            return null;

        var windowDays = DeliveryModeSettings.Value(_configuration, booking.DeliveryMode, "QualityScoreWindowDays", 14);
        var minSamples = DeliveryModeSettings.Value(
            _configuration, booking.DeliveryMode, "QualityScoreMinSamples", DriverQualityScore.DefaultMinSamples);

        var stats = await _offerRepository.GetResponseStatsAsync(
            candidates.Select(c => c.DriverId).ToList(),
            DateTime.UtcNow.AddDays(-windowDays),
            ct);

        return candidate => weight * DriverQualityScore.From(
            stats.TryGetValue(candidate.DriverId, out var s) ? s : default,
            minSamples);
    }

    /// <summary>Where the booking is collected from, if it has coordinates.</summary>
    private static GeoPoint? PickupPoint(Domain.Booking booking)
    {
        var stop = booking.Stops
            .Where(s => s.Type == Domain.StopType.Pickup && s.Latitude.HasValue && s.Longitude.HasValue)
            .OrderBy(s => s.Sequence)
            .FirstOrDefault();

        return stop is null ? null : new GeoPoint(stop.Latitude!.Value, stop.Longitude!.Value);
    }

    /// <summary>
    /// Where the driver ends up.
    /// </summary>
    /// <remarks>
    /// The <i>last</i> dropoff by sequence, deliberately not the legacy <c>Booking.DropoffLatitude</c>
    /// columns, which hold the <i>first</i>. Pooling asks "where does this leave the driver", so the
    /// first of several dropoffs would score a job as compatible on the strength of a leg the driver
    /// has not finished.
    /// </remarks>
    private static GeoPoint? FinalDropoffPoint(Domain.Booking booking)
    {
        var stop = booking.Stops
            .Where(s => s.Type == Domain.StopType.Dropoff && s.Latitude.HasValue && s.Longitude.HasValue)
            .OrderByDescending(s => s.Sequence)
            .FirstOrDefault();

        return stop is null ? null : new GeoPoint(stop.Latitude!.Value, stop.Longitude!.Value);
    }

    /// <summary>
    /// Primary path: nearest drivers from the H3 index, verified against Identity.
    /// Drivers that are not reporting locations do not appear here by design —
    /// they are only reachable through the fallback scan.
    /// </summary>
    private async Task<IReadOnlyList<AvailableDriver>> GetFromGeoIndexAsync(
        Domain.Booking booking,
        decimal proximityRadiusKm,
        int maxRings,
        CancellationToken ct)
    {
        var bookingId = booking.Id;
        var pickupLatitude = booking.PickupLatitude!.Value;
        var pickupLongitude = booking.PickupLongitude!.Value;
        var maxOffers = _configuration.GetValue("BookingSettings:MaxOffersPerBroadcast", 15);
        // Over-fetch so that candidates dropped by the Identity check (inactive,
        // not a driver) still leave enough to fill the offer list.
        var candidateTarget = Math.Max(maxOffers * 3, 30);

        var candidates = await _geoIndex.FindNearestDriversAsync(
            pickupLatitude, pickupLongitude, candidateTarget, maxRings, ct);

        if (candidates.Count == 0)
            return Array.Empty<AvailableDriver>();

        if (proximityRadiusKm > 0)
            candidates = candidates.Where(c => (decimal)c.DistanceKm <= proximityRadiusKm).ToList();

        if (candidates.Count == 0)
            return Array.Empty<AvailableDriver>();

        var candidateIds = candidates.Select(c => c.DriverId.ToString()).ToList();
        var eligibleIds = (await _userManager.Users
                .Where(u => candidateIds.Contains(u.Id) && u.IsActive && u.IsOnline && u.Role == "Driver" && u.IsOnboarded)
                .Select(u => u.Id)
                .ToListAsync(ct))
            .Select(Guid.Parse)
            .ToHashSet();

        var eligible = candidates
            .Where(c => eligibleIds.Contains(c.DriverId))
            .Select(c => new RankCandidate(c.DriverId, (decimal)c.DistanceKm, c.Latitude, c.Longitude))
            .ToList();

        eligible = await FilterToDriversWhoCanPayCommissionAsync(booking, bookingId, eligible, ct);

        var available = Rank(eligible, await BuildBonusAsync(booking, eligible, ct));

        _logger.LogInformation(
            "H3 geo index found {CandidateCount} candidate(s), {EligibleCount} eligible driver(s) for booking {BookingId}",
            candidates.Count, available.Count, bookingId);

        return available;
    }

    /// <summary>
    /// Fallback path (previous behavior): scan all active drivers and rank by
    /// distance using the coordinates synced to Identity. Includes drivers with
    /// no location data (null distance) unless a proximity radius is configured.
    /// </summary>
    private async Task<IReadOnlyList<AvailableDriver>> GetByFullScanAsync(
        Domain.Booking booking,
        Guid bookingId,
        decimal proximityRadiusKm,
        CancellationToken ct)
    {
        // Get all online, active drivers
        // Note: in the independent-driver model, only Drivers exist (no Owners/Operators)
        var allUsers = await _userManager.Users
            .Where(u => u.IsActive && u.IsOnline && u.Role == "Driver" && u.IsOnboarded)
            .ToListAsync(ct);

        _logger.LogInformation(
            "Driver availability fallback scan for booking {BookingId}: online drivers={OnlineCount}",
            bookingId, allUsers.Count);

        if (allUsers.Count == 0)
        {
            _logger.LogWarning(
                "No available drivers for booking {BookingId}. " +
                "Ensure at least one user has Role=\"Driver\", IsActive=true, IsOnline=true and IsOnboarded=true.",
                bookingId);
            return Array.Empty<AvailableDriver>();
        }

        var candidates = new List<RankCandidate>();
        var driverLocationsForDistance = new List<(Guid DriverId, decimal Lat, decimal Lon)>();

        foreach (var driverUser in allUsers)
        {
            var driverId = Guid.Parse(driverUser.Id);

            // Collect driver location for distance calculation
            if (booking.PickupLatitude.HasValue &&
                booking.PickupLongitude.HasValue &&
                driverUser.CurrentLatitude.HasValue &&
                driverUser.CurrentLongitude.HasValue)
            {
                driverLocationsForDistance.Add((
                    driverId,
                    driverUser.CurrentLatitude.Value,
                    driverUser.CurrentLongitude.Value
                ));
            }
            else
            {
                // Driver is available but no location data - add with null distance
                candidates.Add(new RankCandidate(driverId, null, driverUser.CurrentLatitude, driverUser.CurrentLongitude));
            }
        }

        // Calculate distances for drivers with location data
        if (driverLocationsForDistance.Any() &&
            booking.PickupLatitude.HasValue &&
            booking.PickupLongitude.HasValue)
        {
            var distances = await _distanceService.CalculateDistancesToPointAsync(
                booking.PickupLatitude.Value,
                booking.PickupLongitude.Value,
                driverLocationsForDistance,
                ct);

            var positions = driverLocationsForDistance.ToDictionary(d => d.DriverId, d => (d.Lat, d.Lon));
            foreach (var (driverId, distance) in distances)
            {
                var position = positions.TryGetValue(driverId, out var p)
                    ? ((decimal?)p.Lat, (decimal?)p.Lon)
                    : (null, null);
                candidates.Add(new RankCandidate(driverId, distance, position.Item1, position.Item2));
            }
        }

        candidates = await FilterToDriversWhoCanPayCommissionAsync(booking, bookingId, candidates, ct);

        var sortedDrivers = Rank(candidates, await BuildBonusAsync(booking, candidates, ct));

        // Optional geo-fencing: exclude drivers farther than ProximityRadiusKm from pickup.
        // Drivers without location (DistanceKm null) are excluded when radius is set.
        if (proximityRadiusKm > 0)
        {
            var before = sortedDrivers.Count;
            sortedDrivers = sortedDrivers
                .Where(d => d.DistanceKm.HasValue && d.DistanceKm.Value <= proximityRadiusKm)
                .Select((driver, index) => driver with { SequenceNumber = index + 1 })
                .ToList();
            if (sortedDrivers.Count < before)
                _logger.LogInformation(
                    "Proximity filter: {Excluded} driver(s) excluded (>{Radius} km from pickup) for booking {BookingId}. Remaining: {Remaining}.",
                    before - sortedDrivers.Count, proximityRadiusKm, bookingId, sortedDrivers.Count);
        }

        _logger.LogInformation(
            "Found {Count} available drivers for booking {BookingId} (fallback scan)",
            sortedDrivers.Count,
            bookingId);

        return sortedDrivers;
    }

    /// <summary>
    /// Drops candidates who could not pay the platform commission on this booking (issue #102).
    /// </summary>
    /// <remarks>
    /// Every booking is paid in cash, so the driver collects the whole fare and we sweep our share
    /// out of their wallet afterwards. Offering a job to a driver whose wallet cannot cover that is
    /// how a debt gets created, and a debt on a wallet we cannot overdraw is one we may never
    /// collect — so the check belongs here, before the offer, not only after the delivery.
    ///
    /// <para>
    /// Asked of the Drivers module rather than read directly: wallets are theirs, and the commission
    /// rate lives with them, so nothing here needs to know it. One call for the whole candidate set.
    /// </para>
    /// <para>
    /// A failure to answer leaves the candidates alone. Losing dispatch entirely because the wallet
    /// query failed would be a far worse outcome than a commission we have to chase.
    /// </para>
    /// </remarks>
    private async Task<List<RankCandidate>> FilterToDriversWhoCanPayCommissionAsync(
        Domain.Booking booking,
        Guid bookingId,
        List<RankCandidate> candidates,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
            return candidates;

        // FinalFare once it is known, EstimatedFare while the booking is still being matched — which
        // is when this runs. The two can differ, so the sweep after delivery may ask for slightly
        // more than was checked here; that gap is what the arrears path exists for.
        //
        // An unpriced booking (fare 0) is passed through rather than skipped: the commission on it
        // is zero, so every funded driver trivially clears it, while a driver who already owes us
        // from an earlier job is still held back. Skipping would quietly hand those drivers work.
        var fare = booking.FinalFare ?? booking.EstimatedFare;

        IReadOnlyList<Guid> payable;
        try
        {
            payable = await _mediator.Send(
                new GetDriversEligibleForCashJobQuery(
                    candidates.Select(c => c.DriverId).ToList(), fare),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Wallet eligibility check failed for booking {BookingId}; offering to all {Count} candidate(s)",
                bookingId, candidates.Count);
            return candidates;
        }

        var payableIds = payable.ToHashSet();
        var filtered = candidates.Where(c => payableIds.Contains(c.DriverId)).ToList();

        var excluded = candidates.Count - filtered.Count;
        if (excluded > 0)
        {
            // Logged and counted because the alternative is indistinguishable from "no drivers
            // nearby": a booking that reaches nobody because every candidate is out of funds looks
            // exactly like one in an empty area, and only this line tells them apart.
            BeeLogistics.Shared.Infrastructure.BeeMetrics.CashJobsBlockedByWallet.Add(excluded);
            _logger.LogInformation(
                "Wallet filter: {Excluded} driver(s) excluded (cannot cover the commission on a "
                + "{Fare} fare) for booking {BookingId}. Remaining: {Remaining}.",
                excluded, fare, bookingId, filtered.Count);
        }

        return filtered;
    }
}
