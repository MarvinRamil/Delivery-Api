using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Bookings.Infrastructure.Repositories;

public class BookingRepository : Repository<Booking, BookingsDbContext>, IBookingRepository
{
    public BookingRepository(BookingsDbContext context) : base(context) { }

    public override async Task<Booking?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await DbSet
            .Include(b => b.Customer)
            .Include(b => b.Stops) // Include stops for multi-stop support
            .FirstOrDefaultAsync(b => b.Id == id, ct);

    /// <inheritdoc />
    public async Task<Booking?> GetByIdWithProofOfDeliveriesAsync(Guid id, CancellationToken ct = default)
        => await DbSet
            .Include(b => b.Customer)
            .Include(b => b.Stops)
            .Include(b => b.ProofOfDeliveries)
            .FirstOrDefaultAsync(b => b.Id == id, ct);

    public override async Task<IReadOnlyList<Booking>> GetAllAsync(CancellationToken ct = default)
        => await DbSet
            .Include(b => b.Customer)
            .ToListAsync(ct);

    public async Task<Booking?> GetByBookingNumberAsync(string bookingNumber, CancellationToken ct = default)
        => await DbSet
            .Include(b => b.Customer)
            .FirstOrDefaultAsync(b => b.BookingNumber == bookingNumber, ct);

    public async Task<IReadOnlyList<Booking>> GetCreatedSinceAsync(DateTime sinceUtc, int limit, CancellationToken ct = default)
        => await DbSet
            .AsNoTracking()
            .Where(b => b.CreatedAt >= sinceUtc)
            .OrderBy(b => b.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Booking>> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default)
        => await DbSet
            .Include(b => b.Customer)
            .Include(b => b.Stops) // Include stops for multi-stop support
            .Where(b => b.CustomerId == customerId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Booking>> GetByStatusAsync(BookingStatus status, CancellationToken ct = default)
        => await DbSet
            .Include(b => b.Customer)
            .Where(b => b.Status == status)
            .ToListAsync(ct);

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately keeps its newest-first ordering and stays uncapped. This backs an operator
    /// listing, not dispatch — the pulse job uses
    /// <see cref="GetPendingAssignmentDueForDispatchAsync"/>, which is ordered by dispatch rank,
    /// capped, bounded to recent bookings, and skips the Customer join. Reordering this one to suit
    /// the job would silently rearrange a back-office screen that never asked for it.
    /// </remarks>
    public async Task<IReadOnlyList<Booking>> GetPendingAssignmentBookingsAsync(CancellationToken ct = default)
        => await DbSet
            .Include(b => b.Customer)
            .Where(b => b.AssignmentStatus == BookingAssignmentStatus.PendingAssignment)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);

    // No .Include(b => b.Customer) on any of the three dispatch queries below: they only feed the
    // background broadcast/pulse jobs (BookingBroadcastQueueService), which never read Customer —
    // pulling it in would be a wasted join on every tick.

    /// <inheritdoc />
    public async Task<IReadOnlyList<Booking>> GetBroadcastingBookingsAsync(int limit, CancellationToken ct = default)
        => await InDispatchOrder(DbSet
                .Where(b => b.AssignmentStatus == BookingAssignmentStatus.BroadcastingToDrivers))
            .Take(limit)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Booking>> GetDueForPulseAsync(DateTime asOf, int limit, CancellationToken ct = default)
        => await InDispatchOrder(DbSet
                .Where(b => b.AssignmentStatus == BookingAssignmentStatus.BroadcastingToDrivers
                    && (b.NextPulseAt == null || b.NextPulseAt <= asOf)))
            .Take(limit)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Booking>> GetPendingAssignmentDueForDispatchAsync(
        DateTime createdSince, int limit, CancellationToken ct = default)
        => await InDispatchOrder(DbSet
                .Where(b => b.AssignmentStatus == BookingAssignmentStatus.PendingAssignment
                    && b.CreatedAt >= createdSince))
            .Take(limit)
            .ToListAsync(ct);

    /// <summary>
    /// The cross-booking dispatch order: rank first, then most-overdue, then oldest.
    /// </summary>
    /// <remarks>
    /// <see cref="Booking.DispatchPriority"/> rather than <see cref="Booking.DeliveryMode"/>:
    /// the mode is stored as a string, so ordering by it would sort alphabetically
    /// (Pooling &lt; OnDemand &lt; Regular) — silently backwards.
    ///
    /// The COALESCE on NextPulseAt is load-bearing: PostgreSQL sorts NULLs <i>last</i> on ASC, so a
    /// plain <c>ThenBy(b => b.NextPulseAt)</c> would push an unpulsed booking to the back of the
    /// queue — the exact inverse of the "null means due now" meaning the column was given, and the
    /// same meaning the WHERE clauses above rely on. Two kinds of row reach it: bookings that were
    /// already broadcasting when AddBookingPulseSchedule added the column nullable and unbackfilled,
    /// and every PendingAssignment booking, which has never been pulsed at all.
    ///
    /// The sentinel carries <see cref="DateTimeKind.Utc"/> because the column is
    /// <c>timestamp with time zone</c> and Npgsql refuses to write an Unspecified DateTime to one —
    /// a bare <c>DateTime.MinValue</c> would throw here in production while passing in-memory tests.
    ///
    /// Note on cost: the partial index IX_Bookings_DispatchPriority_NextPulseAt matches the filter
    /// and the leading sort keys, but the CreatedAt tiebreak means PostgreSQL may still add a sort
    /// node. That is accepted deliberately — the set is scoped to one fast-changing assignment
    /// status, so it is small, and FIFO within a priority band is worth more than saving a sort on
    /// a handful of rows.
    /// </remarks>
    private static IQueryable<Booking> InDispatchOrder(IQueryable<Booking> query)
        => query
            .OrderByDescending(b => b.DispatchPriority)
            .ThenBy(b => b.NextPulseAt ?? DueNow)
            .ThenBy(b => b.CreatedAt);

    /// <summary>Sorts ahead of every real timestamp, so an unset NextPulseAt reads as "due now".</summary>
    private static readonly DateTime DueNow = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

    // Projects in the database rather than materialising Booking entities: this crosses a module
    // boundary into Payment, which needs five columns, not the aggregate. No Include for the same
    // reason as the broadcast queries above.
    public async Task<IReadOnlyList<CancelledBookingInfo>> GetCancelledSinceAsync(
        DateTime sinceUtc, int limit, CancellationToken ct = default)
        => await DbSet
            .Where(b => b.Status == BookingStatus.Cancelled && b.CancelledAt != null && b.CancelledAt >= sinceUtc)
            .OrderBy(b => b.CancelledAt)
            .Take(limit)
            .Select(b => new CancelledBookingInfo(
                b.Id,
                b.CancelledBy,
                b.SelectedDriverId,
                b.CancelledAt!.Value,
                b.CancellationReason))
            .ToListAsync(ct);

    public async Task<Booking?> FindDuplicateBookingAsync(
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
        CancellationToken ct = default)
    {
        return await DbSet
            .Where(b => b.CustomerId == customerId &&
                       b.PickupLocation == pickupLocation &&
                       b.DropoffLocation == dropoffLocation &&
                       b.VehicleType == truckType &&
                       b.ScheduleDate == scheduleDate &&
                       (b.WeightKg == weightKg || (b.WeightKg == null && weightKg == null)) &&
                       (b.PickupLatitude == pickupLatitude || (b.PickupLatitude == null && pickupLatitude == null)) &&
                       (b.PickupLongitude == pickupLongitude || (b.PickupLongitude == null && pickupLongitude == null)) &&
                       (b.DropoffLatitude == dropoffLatitude || (b.DropoffLatitude == null && dropoffLatitude == null)) &&
                       (b.DropoffLongitude == dropoffLongitude || (b.DropoffLongitude == null && dropoffLongitude == null)) &&
                       b.CreatedAt >= createdAfter)
            .OrderByDescending(b => b.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Booking>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
            return Array.Empty<Booking>();

        return await DbSet
            .Include(b => b.Customer)
            .Where(b => idList.Contains(b.Id))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<(int Total, int Pending, int Active, int Completed, int Cancelled)> GetDashboardCountsAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = DbSet.AsQueryable();
        if (from.HasValue)
            query = query.Where(b => b.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(b => b.CreatedAt < to.Value.Date.AddDays(1));

        var total = await query.CountAsync(ct);
        var pending = await query.CountAsync(b => b.Status == BookingStatus.Pending, ct);
        var active = await query.CountAsync(b =>
            b.Status == BookingStatus.DriverAssigned ||
            b.Status == BookingStatus.OnTheWayToPickup ||
            b.Status == BookingStatus.InProgress ||
            b.Status == BookingStatus.Delivered, ct);
        var completed = await query.CountAsync(b => b.Status == BookingStatus.Completed, ct);
        var cancelled = await query.CountAsync(b => b.Status == BookingStatus.Cancelled, ct);

        return (total, pending, active, completed, cancelled);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Booking>> GetRecentForDashboardAsync(DateTime? from, DateTime? to, int take, CancellationToken ct = default)
    {
        var query = DbSet
            .Include(b => b.Customer)
            .AsNoTracking()
            .AsQueryable();
        if (from.HasValue)
            query = query.Where(b => b.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(b => b.CreatedAt < to.Value.Date.AddDays(1));

        return await query
            .OrderByDescending(b => b.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<Booking> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var query = DbSet.Include(b => b.Customer).AsQueryable();

        var totalCount = await query.CountAsync(ct);
        var skip = (page - 1) * pageSize;
        var items = await query
            .OrderByDescending(b => b.CreatedAt)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, totalCount);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Booking>> GetCompletedByDriverIdAsync(Guid driverId, DateTime? startDate, DateTime? endDate, CancellationToken ct = default)
    {
        var query = DbSet
            .Include(b => b.Customer)
            .Where(b => b.SelectedDriverId == driverId && b.Status == BookingStatus.Completed);
        if (startDate.HasValue)
            query = query.Where(b => b.CreatedAt >= startDate.Value);
        if (endDate.HasValue)
            query = query.Where(b => b.CreatedAt <= endDate.Value);
        return await query.OrderByDescending(b => b.CreatedAt).ToListAsync(ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every booking assigned to the driver, cancelled ones included. This deliberately does not
    /// filter by status: it backs the driver app's trip history, where a cancelled job is part of
    /// the record and not noise. Callers that only want live work must narrow it themselves -
    /// <see cref="Application.Services.LiveTrackingAuthorizer"/> is the worked example.
    ///
    /// A status predicate used to live here, which is why cancelled trips were invisible to the
    /// driver they were taken from while staying visible to the customer who cancelled them
    /// (<see cref="GetByCustomerIdAsync"/> never filtered).
    /// </remarks>
    public async Task<IReadOnlyList<Booking>> GetByDriverIdAsync(Guid driverId, CancellationToken ct = default)
    {
        return await DbSet
            .Include(b => b.Customer)
            .Include(b => b.Stops) // Include stops for multi-stop support
            .Where(b => b.SelectedDriverId == driverId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<DriverWorkload> GetDriverWorkloadAsync(
        Guid driverId, DateTime imminentThreshold, CancellationToken ct = default)
    {
        var rows = await DbSet
            .Where(b => b.SelectedDriverId == driverId && HeldStatuses.Contains(b.Status))
            .GroupBy(b => b.Status == BookingStatus.Confirmed && b.ScheduleDate > imminentThreshold)
            .Select(g => new { IsScheduledAhead = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return new DriverWorkload(
            rows.Where(r => !r.IsScheduledAhead).Sum(r => r.Count),
            rows.Where(r => r.IsScheduledAhead).Sum(r => r.Count));
    }

    /// <inheritdoc />
    public async Task<IDriverCapacityScope> BeginDriverCapacityScopeAsync(
        Guid driverId, CancellationToken ct = default)
    {
        // Advisory locks are Postgres-specific and need a transaction to be scoped to. The
        // in-memory provider used in tests has neither, and nesting a transaction inside one the
        // caller already opened would throw — in both cases fall through to the plain check.
        if (!Context.Database.IsNpgsql() || Context.Database.CurrentTransaction != null)
            return NoOpDriverCapacityScope.Instance;

        var transaction = await Context.Database.BeginTransactionAsync(ct);
        try
        {
            await Context.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock({0}, {1})",
                new object[] { DriverCapacityLockNamespace, DriverLockKey(driverId) },
                ct);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }

        return new PostgresDriverCapacityScope(transaction);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DriverHeldLeg>> GetHeldLegsForDriversAsync(
        IReadOnlyCollection<Guid> driverIds, CancellationToken ct = default)
    {
        if (driverIds.Count == 0)
            return Array.Empty<DriverHeldLeg>();

        var ids = driverIds.ToList();

        // One query. Npgsql renders the SelectMany over Stops as a JOIN LATERAL, verified against a
        // real database — the in-memory provider translates shapes Npgsql will not, so this is not
        // something to take on trust.
        //
        // Picking the lowest-sequence stop per booking is done in memory rather than as a
        // per-booking Take(1), which would add an ORDER BY + LIMIT inside the lateral subquery for
        // no benefit here: the row count is already bounded by (candidates x 3 held bookings x 2
        // stops), and it stays one round trip either way — that being the invariant that matters
        // on a 15-second tick.
        var stops = await DbSet
            .Where(b => b.SelectedDriverId != null
                && ids.Contains(b.SelectedDriverId.Value)
                && HeldStatuses.Contains(b.Status))
            .SelectMany(b => b.Stops
                .Where(s => s.Status != StopStatus.Completed
                    && s.Latitude != null
                    && s.Longitude != null)
                .Select(s => new
                {
                    DriverId = b.SelectedDriverId!.Value,
                    BookingId = b.Id,
                    s.Sequence,
                    Latitude = s.Latitude!.Value,
                    Longitude = s.Longitude!.Value,
                }))
            .ToListAsync(ct);

        return stops
            .GroupBy(s => s.BookingId)
            .Select(g => g.OrderBy(s => s.Sequence).First())
            .Select(s => new DriverHeldLeg(s.DriverId, s.BookingId, s.Latitude, s.Longitude))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, DeliveryMode>> GetDeliveryModesAsync(
        IEnumerable<Guid> bookingIds, CancellationToken ct = default)
    {
        var ids = bookingIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, DeliveryMode>();

        var rows = await DbSet
            .Where(b => ids.Contains(b.Id))
            .Select(b => new { b.Id, b.DeliveryMode })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id, r => r.DeliveryMode);
    }

    /// <summary>
    /// Statuses in which a booking still occupies its driver. Mirrors
    /// <see cref="DriverCapacityPolicy.HeldStatuses"/>; held as a local array so EF translates
    /// the Contains into a SQL IN rather than evaluating it client-side.
    /// </summary>
    private static readonly BookingStatus[] HeldStatuses = DriverCapacityPolicy.HeldStatuses;

    /// <summary>
    /// First key of the two-key advisory lock: a fixed namespace ("BEE1") so a driver's
    /// capacity lock cannot collide with an advisory lock taken for anything else.
    /// </summary>
    private const int DriverCapacityLockNamespace = 0x42454531;

    /// <summary>
    /// Advisory locks take int keys, so the driver's Guid is folded into 32 bits. A collision
    /// only ever costs two unrelated drivers a brief serialisation, never a wrong answer.
    /// </summary>
    private static int DriverLockKey(Guid driverId) => BitConverter.ToInt32(driverId.ToByteArray(), 0);
}
