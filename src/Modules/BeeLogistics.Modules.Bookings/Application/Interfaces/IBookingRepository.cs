using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;

namespace BeeLogistics.Modules.Bookings.Application.Interfaces;

public interface IBookingRepository : IRepository<Booking>
{
    Task<Booking?> GetByBookingNumberAsync(string bookingNumber, CancellationToken ct = default);

    /// <summary>
    /// Bookings created at or after <paramref name="sinceUtc"/>, oldest first, capped at
    /// <paramref name="limit"/>. Backs the chat-room backstop sweep (#78).
    /// </summary>
    Task<IReadOnlyList<Booking>> GetCreatedSinceAsync(DateTime sinceUtc, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<Booking>> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default);
    Task<IReadOnlyList<Booking>> GetByStatusAsync(BookingStatus status, CancellationToken ct = default);
    /// <summary>
    /// Every booking awaiting assignment, newest first. Backs the operator listing; dispatch uses
    /// <see cref="GetPendingAssignmentDueForDispatchAsync"/> instead.
    /// </summary>
    Task<IReadOnlyList<Booking>> GetPendingAssignmentBookingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Bookings currently broadcasting, in dispatch order, capped at <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<Booking>> GetBroadcastingBookingsAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// Bookings in BroadcastingToDrivers whose NextPulseAt is due (or unset), in dispatch order,
    /// capped at <paramref name="limit"/>. Due-only variant of
    /// <see cref="GetBroadcastingBookingsAsync"/> used by the recurring pulse job so it only
    /// reprocesses bookings that actually need it.
    /// </summary>
    Task<IReadOnlyList<Booking>> GetDueForPulseAsync(DateTime asOf, int limit, CancellationToken ct = default);

    /// <summary>
    /// Bookings still awaiting their first assignment and created at or after
    /// <paramref name="createdSince"/>, in dispatch order, capped at <paramref name="limit"/>.
    /// </summary>
    /// <remarks>
    /// The pulse job's safety net for bookings whose initial broadcast message was never consumed.
    /// Separate from <see cref="GetPendingAssignmentBookingsAsync"/> because the two want opposite
    /// things: the operator listing wants everything, newest first; the job wants a small, ranked,
    /// recent slice with no Customer join. The recency bound used to be applied in memory after
    /// loading every PendingAssignment booking ever created.
    /// </remarks>
    Task<IReadOnlyList<Booking>> GetPendingAssignmentDueForDispatchAsync(
        DateTime createdSince, int limit, CancellationToken ct = default);

    /// <summary>
    /// Bookings cancelled at or after <paramref name="sinceUtc"/>, oldest first, capped at
    /// <paramref name="limit"/>. Feeds the payment reconciliation sweep (GitLab #51).
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="GetByStatusAsync"/>: that has no date bound, no cap, and joins
    /// Customer, so on a mature database it would load every booking ever cancelled on an hourly job.
    /// </remarks>
    Task<IReadOnlyList<CancelledBookingInfo>> GetCancelledSinceAsync(DateTime sinceUtc, int limit, CancellationToken ct = default);
    Task<Booking?> FindDuplicateBookingAsync(
        Guid customerId,
        string pickupLocation,
        string dropoffLocation,
        string truckType,
        DateTime scheduleDate,
        decimal? weightKg,
        decimal? pickupLatitude,
        decimal? pickupLongitude,
        decimal? dropoffLatitude,
        decimal? dropoffLongitude,
        DateTime createdAfter,
        CancellationToken ct = default);
    
    /// <summary>
    /// Gets multiple bookings by their IDs in a single query.
    /// Use this instead of looping with GetByIdAsync().
    /// </summary>
    Task<IReadOnlyList<Booking>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Gets booking counts for dashboard stats (DB-side filtering by date).
    /// </summary>
    Task<(int Total, int Pending, int Active, int Completed, int Cancelled)> GetDashboardCountsAsync(DateTime? from, DateTime? to, CancellationToken ct = default);

    /// <summary>
    /// Gets recent bookings for dashboard (DB-side, limited).
    /// </summary>
    Task<IReadOnlyList<Booking>> GetRecentForDashboardAsync(DateTime? from, DateTime? to, int take, CancellationToken ct = default);

    /// <summary>
    /// Gets bookings with server-side pagination. When tenantId is null, returns all; otherwise filtered by tenant.
    /// </summary>
    Task<(IReadOnlyList<Booking> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Gets completed bookings for a driver (for earnings calculation). Optional date range filter.
    /// </summary>
    Task<IReadOnlyList<Booking>> GetCompletedByDriverIdAsync(Guid driverId, DateTime? startDate, DateTime? endDate, CancellationToken ct = default);

    /// <summary>
    /// Gets all bookings assigned to a driver (by SelectedDriverId). Includes all statuses except Cancelled.
    /// </summary>
    Task<IReadOnlyList<Booking>> GetByDriverIdAsync(Guid driverId, CancellationToken ct = default);

    /// <summary>
    /// Gets a booking by ID including ProofOfDeliveries (for serving POD/signature images).
    /// </summary>
    Task<Booking?> GetByIdWithProofOfDeliveriesAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Counts the bookings one driver is already holding, split into active work and future
    /// claims at <paramref name="imminentThreshold"/>. Feeds the concurrency caps (#55).
    /// </summary>
    Task<DriverWorkload> GetDriverWorkloadAsync(Guid driverId, DateTime imminentThreshold, CancellationToken ct = default);

    /// <summary>
    /// Opens a scope that serializes this driver's accepts. See <see cref="IDriverCapacityScope"/>.
    /// </summary>
    Task<IDriverCapacityScope> BeginDriverCapacityScopeAsync(Guid driverId, CancellationToken ct = default);

    /// <summary>
    /// The next stop each of these drivers still owes, one row per held booking.
    /// </summary>
    /// <remarks>
    /// Feeds the pooling detour bias. This runs inside candidate selection on a 15-second job, so it
    /// is <b>one</b> projected query for the whole candidate set and materialises no
    /// <see cref="Booking"/> entities. One row per held booking rather than per driver, because a
    /// driver may hold up to three and the best fit could be any of them.
    /// </remarks>
    Task<IReadOnlyList<DriverHeldLeg>> GetHeldLegsForDriversAsync(
        IReadOnlyCollection<Guid> driverIds, CancellationToken ct = default);

    /// <summary>
    /// The delivery mode of each of these bookings.
    /// </summary>
    /// <remarks>
    /// Exists so the offer-rejected and offer-expired metrics can carry a <c>mode</c> tag without
    /// loading aggregates. Pooling reduces a driver's pay per job, so elevated pooled rejects are
    /// expected — untagged they would look like a dispatch bug instead. A single-column projection,
    /// no Customer join, no entity materialisation.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, DeliveryMode>> GetDeliveryModesAsync(
        IEnumerable<Guid> bookingIds, CancellationToken ct = default);
}
