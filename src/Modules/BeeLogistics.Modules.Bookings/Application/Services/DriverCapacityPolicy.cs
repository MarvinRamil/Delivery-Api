using BeeLogistics.Modules.Bookings.Domain;
using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// How much work a driver is already holding, split along the two limits that apply to it.
/// </summary>
/// <param name="Active">Bookings the driver owes a customer now: work in motion, plus confirmed
/// work whose slot has arrived or is imminent.</param>
/// <param name="ScheduledAhead">Confirmed bookings whose slot is still far enough away that they
/// do not compete for the driver's time yet.</param>
public readonly record struct DriverWorkload(int Active, int ScheduledAhead);

/// <summary>
/// The next stop one driver still owes on one booking they are already holding — the anchor the
/// pooling detour bias measures against. See <see cref="PoolingCompatibility"/>.
/// </summary>
public readonly record struct DriverHeldLeg(
    Guid DriverId,
    Guid BookingId,
    decimal Latitude,
    decimal Longitude);

/// <summary>The two caps and the boundary between them, read from <c>BookingSettings</c>.</summary>
public readonly record struct DriverCapacityLimits(
    int MaxActive,
    int MaxScheduledAhead,
    int ImminentWindowMinutes);

/// <summary>
/// Caps how many bookings one driver can hold at once (issue #55).
/// </summary>
/// <remarks>
/// Before this existed there was no cap anywhere, so a single driver could accept five, ten or
/// twenty bookings. Every one of those customers saw a driver assigned and assumed they were on
/// the way, while the driver could physically serve only a few — the failure landed on the
/// customer as a late or never-arriving delivery rather than as a rejected request.
///
/// Two limits rather than one, because they constrain different things:
/// <list type="bullet">
/// <item><b>Active work</b> competes for the driver's next few hours. Kept low (3).</item>
/// <item><b>Future claims</b> are a driver planning their diary, which is worth encouraging.
/// Kept looser (5).</item>
/// </list>
///
/// The policy deliberately makes no judgement about whether two jobs are on a compatible route.
/// Distance alone cannot tell a good batch from a bad one — two pickups 2 km apart heading in
/// opposite directions look identical to one that is on the way — and the driver, who can see
/// every address and both fares on the offer, is far better placed to decide. The cap only stops
/// one driver silently absorbing a queue nobody else can serve.
///
/// Enforced at accept only, deliberately. Drivers at the cap keep receiving and browsing offers —
/// they can see what is going around while they finish the work they already hold — and are
/// turned down at the moment they try to take one, with a message saying which limit they hit.
/// Filtering them out of the broadcast instead would have hidden the market from exactly the
/// drivers about to re-enter it.
/// </remarks>
public static class DriverCapacityPolicy
{
    /// <summary>Bookings in progress at once. Three separate jobs, each with its own pickup —
    /// multi-drop within one job is already modelled by <see cref="Booking.Stops"/>.</summary>
    public const int DefaultMaxActive = 3;

    /// <summary>Confirmed bookings held for a future slot.</summary>
    public const int DefaultMaxScheduledAhead = 5;

    /// <summary>
    /// How close a slot must be before a claim counts against the active limit instead of the
    /// scheduled one. Shares <c>ScheduledClaimLeadMinutes</c> with the give-up deadline added in
    /// #50, so "imminent enough to need a driver" and "imminent enough to occupy one" agree.
    /// </summary>
    public const int DefaultImminentWindowMinutes = 45;

    /// <summary>
    /// Statuses in which a driver still owes the customer a delivery. Cancelled and Completed
    /// free capacity immediately by falling out of this set.
    /// </summary>
    public static readonly BookingStatus[] HeldStatuses =
    {
        BookingStatus.Confirmed,
        BookingStatus.DriverAssigned,
        BookingStatus.PickedUp,
        BookingStatus.InTransit,
    };

    public static DriverCapacityLimits ReadLimits(IConfiguration configuration) => new(
        configuration.GetValue("BookingSettings:MaxActiveBookingsPerDriver", DefaultMaxActive),
        configuration.GetValue("BookingSettings:MaxScheduledClaimsPerDriver", DefaultMaxScheduledAhead),
        configuration.GetValue("BookingSettings:ScheduledClaimLeadMinutes", DefaultImminentWindowMinutes));

    /// <summary>
    /// The moment after which a confirmed booking is a future claim rather than active work.
    /// </summary>
    public static DateTime ImminentThreshold(DateTime now, DriverCapacityLimits limits)
        => now.AddMinutes(limits.ImminentWindowMinutes);

    /// <summary>
    /// Whether a booking's slot is far enough out to count against the scheduled-claims limit.
    /// </summary>
    public static bool IsScheduledAhead(DateTime scheduleDate, DateTime imminentThreshold)
        => NormalizeToUtc(scheduleDate) > imminentThreshold;

    /// <summary>
    /// ScheduleDate reaches us as Unspecified on some paths; treating it as local would shift the
    /// boundary by the server's offset. Same normalisation the give-up deadline uses.
    /// </summary>
    public static DateTime NormalizeToUtc(DateTime value)
        => value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

    /// <summary>
    /// Whether the driver is already at the limit that governs a booking of this kind.
    /// </summary>
    /// <remarks>
    /// The two counts are checked independently: a driver holding five future claims can still
    /// take work for today, and a driver mid-delivery can still claim next Tuesday.
    /// </remarks>
    public static bool IsAtCapacity(DriverWorkload workload, DriverCapacityLimits limits, bool bookingIsScheduledAhead)
        => bookingIsScheduledAhead
            ? workload.ScheduledAhead >= limits.MaxScheduledAhead
            : workload.Active >= limits.MaxActive;

    /// <summary>
    /// Why this accept must be refused, or null when it fits. The message reaches the driver's
    /// app as the rejection reason, so it says which limit was hit and how to free capacity.
    /// </summary>
    public static string? RejectionReason(DriverWorkload workload, DriverCapacityLimits limits, bool bookingIsScheduledAhead)
    {
        if (!IsAtCapacity(workload, limits, bookingIsScheduledAhead))
            return null;

        return bookingIsScheduledAhead
            ? $"You already hold {workload.ScheduledAhead} upcoming scheduled bookings (limit {limits.MaxScheduledAhead}). "
              + "Complete or release one before claiming another."
            : $"You already have {workload.Active} active bookings (limit {limits.MaxActive}). "
              + "Complete or cancel one before accepting another.";
    }
}
