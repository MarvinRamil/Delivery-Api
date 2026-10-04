using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Bookings.Infrastructure;
using BeeLogistics.Modules.Bookings.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// Cross-booking dispatch order. On-Demand customers pay a premium for being served first, so this
/// is the query that has to actually deliver it — and it runs against the real repository rather
/// than a substitute, because the whole risk here lives in translation and ordering rather than in
/// the calling code.
///
/// Two of these assertions guard mistakes that produce no error at all:
/// <list type="bullet">
/// <item>ordering by <c>DeliveryMode</c> instead of <c>DispatchPriority</c> sorts the modes
/// alphabetically — Pooling, OnDemand, Regular — which is backwards, and looks like working code
/// because the column is a string;</item>
/// <item><c>ThenBy(NextPulseAt)</c> without the COALESCE puts NULLs last on PostgreSQL, which
/// inverts the meaning the column was given: a booking with no pulse recorded is the <i>most</i>
/// overdue, not the least.</item>
/// </list>
/// </summary>
public class BookingDispatchOrderingTests
{
    private static BookingsDbContext NewContext()
        => new(new DbContextOptionsBuilder<BookingsDbContext>()
            .UseInMemoryDatabase($"dispatch-ordering-{Guid.NewGuid()}")
            .Options);

    private static async Task<Guid> SeedCustomerAsync(BookingsDbContext context)
    {
        var customer = new Customer(
            "Test Customer", $"customer-{Guid.NewGuid():N}@example.com", "Acme", "09170000000", "Manila");
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        return customer.Id;
    }

    private static Booking NewBooking(Guid customerId, DeliveryMode mode)
        => new(
            customerId,
            "Motorcycle",
            "Documents",
            DateTime.UtcNow.AddHours(1),
            ServiceType.Immediate,
            estimatedFare: 150m,
            stops:
            [
                new DeliveryStop(Guid.Empty, 0, "Pickup St", StopType.Pickup, 16.61m, 120.31m),
                new DeliveryStop(Guid.Empty, 1, "Dropoff Ave", StopType.Dropoff, 16.62m, 120.33m),
            ],
            deliveryMode: mode);

    /// <summary>A booking already broadcasting, optionally with a pulse scheduled and a backdated creation.</summary>
    private static Booking Broadcasting(
        BookingsDbContext context, Guid customerId, DeliveryMode mode,
        DateTime? nextPulseAt = null, DateTime? createdAt = null)
    {
        var booking = NewBooking(customerId, mode);
        booking.StartBroadcastingToDrivers();
        if (nextPulseAt.HasValue)
            booking.SchedulePulse(nextPulseAt.Value);

        context.Bookings.Add(booking);
        if (createdAt.HasValue)
            context.Entry(booking).Property(b => b.CreatedAt).CurrentValue = createdAt.Value;

        return booking;
    }

    /// <summary>
    /// A broadcasting booking with no NextPulseAt at all.
    /// </summary>
    /// <remarks>
    /// Reachable only as a legacy row: <c>StartBroadcastingToDrivers</c> stamps NextPulseAt, so
    /// anything that entered the status after the AddBookingPulseSchedule migration has one. The
    /// column was added nullable with no backfill, so rows that were already broadcasting when it
    /// shipped have NULL — which is what the query's <c>NextPulseAt == null</c> branch is for, and
    /// what the COALESCE in the ordering is for.
    /// </remarks>
    private static Booking BroadcastingUnpulsed(
        BookingsDbContext context, Guid customerId, DeliveryMode mode, DateTime? createdAt = null)
    {
        var booking = Broadcasting(context, customerId, mode, createdAt: createdAt);
        context.Entry(booking).Property(b => b.NextPulseAt).CurrentValue = null;
        return booking;
    }

    // --- The due-for-pulse queue ---

    [Fact]
    public async Task Rank_orders_the_pulse_queue_ahead_of_everything_else()
    {
        await using var context = NewContext();
        var customerId = await SeedCustomerAsync(context);
        var due = DateTime.UtcNow.AddMinutes(-1);

        // Deliberately seeded worst-first: Pooling is both oldest and most overdue, so anything that
        // ignores rank will put it first.
        var pooling = Broadcasting(context, customerId, DeliveryMode.Pooling,
            nextPulseAt: due.AddMinutes(-10), createdAt: DateTime.UtcNow.AddMinutes(-30));
        var regular = Broadcasting(context, customerId, DeliveryMode.Regular,
            nextPulseAt: due.AddMinutes(-5), createdAt: DateTime.UtcNow.AddMinutes(-20));
        var onDemand = Broadcasting(context, customerId, DeliveryMode.OnDemand,
            nextPulseAt: due, createdAt: DateTime.UtcNow.AddMinutes(-1));
        await context.SaveChangesAsync();

        var queue = await new BookingRepository(context).GetDueForPulseAsync(DateTime.UtcNow, limit: 10);

        Assert.Equal(
            new[] { onDemand.Id, regular.Id, pooling.Id },
            queue.Select(b => b.Id).ToArray());
    }

    [Fact]
    public async Task A_booking_with_no_pulse_recorded_is_treated_as_the_most_overdue_in_its_rank()
    {
        // NextPulseAt null means "due now". A plain ThenBy would sort it last on PostgreSQL — the
        // exact inverse — because ASC puts NULLs last there.
        await using var context = NewContext();
        var customerId = await SeedCustomerAsync(context);

        var pulsed = Broadcasting(context, customerId, DeliveryMode.Regular,
            nextPulseAt: DateTime.UtcNow.AddMinutes(-5), createdAt: DateTime.UtcNow.AddMinutes(-30));
        var unpulsed = BroadcastingUnpulsed(context, customerId, DeliveryMode.Regular,
            createdAt: DateTime.UtcNow.AddMinutes(-1));
        await context.SaveChangesAsync();

        var queue = await new BookingRepository(context).GetDueForPulseAsync(DateTime.UtcNow, limit: 10);

        // Note the tie-break it has to beat: `pulsed` is 29 minutes older, so falling through to
        // CreatedAt would put it first.
        Assert.Equal(new[] { unpulsed.Id, pulsed.Id }, queue.Select(b => b.Id).ToArray());
    }

    [Fact]
    public async Task A_booking_with_no_pulse_recorded_is_still_due()
    {
        // The WHERE clause's null branch, not just the ordering. A legacy row that lost this would
        // stop being pulsed entirely and sit in BroadcastingToDrivers until a backstop caught it.
        await using var context = NewContext();
        var customerId = await SeedCustomerAsync(context);

        var unpulsed = BroadcastingUnpulsed(context, customerId, DeliveryMode.Regular);
        await context.SaveChangesAsync();

        var queue = await new BookingRepository(context).GetDueForPulseAsync(DateTime.UtcNow, limit: 10);

        Assert.Equal(unpulsed.Id, Assert.Single(queue).Id);
    }

    [Fact]
    public async Task Bookings_of_equal_rank_and_equal_pulse_are_served_oldest_first()
    {
        await using var context = NewContext();
        var customerId = await SeedCustomerAsync(context);
        var samePulse = DateTime.UtcNow.AddMinutes(-1);

        var newer = Broadcasting(context, customerId, DeliveryMode.Regular,
            nextPulseAt: samePulse, createdAt: DateTime.UtcNow.AddMinutes(-2));
        var older = Broadcasting(context, customerId, DeliveryMode.Regular,
            nextPulseAt: samePulse, createdAt: DateTime.UtcNow.AddMinutes(-20));
        await context.SaveChangesAsync();

        var queue = await new BookingRepository(context).GetDueForPulseAsync(DateTime.UtcNow, limit: 10);

        Assert.Equal(new[] { older.Id, newer.Id }, queue.Select(b => b.Id).ToArray());
    }

    [Fact]
    public async Task The_cap_applies_after_ordering_not_before()
    {
        // The whole point of ordering: under a cap, rank decides who gets served at all.
        await using var context = NewContext();
        var customerId = await SeedCustomerAsync(context);

        for (var i = 0; i < 5; i++)
            Broadcasting(context, customerId, DeliveryMode.Regular,
                createdAt: DateTime.UtcNow.AddMinutes(-30 + i));
        var onDemand = Broadcasting(context, customerId, DeliveryMode.OnDemand,
            createdAt: DateTime.UtcNow);  // newest, so last by every other criterion
        await context.SaveChangesAsync();

        var queue = await new BookingRepository(context).GetDueForPulseAsync(DateTime.UtcNow, limit: 1);

        Assert.Equal(onDemand.Id, Assert.Single(queue).Id);
    }

    [Fact]
    public async Task A_booking_whose_pulse_is_still_in_the_future_is_not_due()
    {
        await using var context = NewContext();
        var customerId = await SeedCustomerAsync(context);

        Broadcasting(context, customerId, DeliveryMode.OnDemand, nextPulseAt: DateTime.UtcNow.AddMinutes(5));
        await context.SaveChangesAsync();

        var queue = await new BookingRepository(context).GetDueForPulseAsync(DateTime.UtcNow, limit: 10);

        Assert.Empty(queue);
    }

    // --- The broadcasting queue (backstop job) ---

    [Fact]
    public async Task The_broadcasting_queue_is_ranked_and_capped_too()
    {
        await using var context = NewContext();
        var customerId = await SeedCustomerAsync(context);

        Broadcasting(context, customerId, DeliveryMode.Regular, createdAt: DateTime.UtcNow.AddMinutes(-30));
        var onDemand = Broadcasting(context, customerId, DeliveryMode.OnDemand, createdAt: DateTime.UtcNow);
        await context.SaveChangesAsync();

        var queue = await new BookingRepository(context).GetBroadcastingBookingsAsync(limit: 1);

        Assert.Equal(onDemand.Id, Assert.Single(queue).Id);
    }

    // --- The pending-assignment safety net ---

    [Fact]
    public async Task The_pending_assignment_sweep_is_ranked_and_bounded_to_recent_bookings()
    {
        await using var context = NewContext();
        var customerId = await SeedCustomerAsync(context);

        // Never broadcast — these are the bookings whose initial message was lost.
        var stale = NewBooking(customerId, DeliveryMode.OnDemand);
        var regular = NewBooking(customerId, DeliveryMode.Regular);
        var onDemand = NewBooking(customerId, DeliveryMode.OnDemand);
        context.Bookings.AddRange(stale, regular, onDemand);
        context.Entry(stale).Property(b => b.CreatedAt).CurrentValue = DateTime.UtcNow.AddHours(-5);
        context.Entry(regular).Property(b => b.CreatedAt).CurrentValue = DateTime.UtcNow.AddMinutes(-30);
        context.Entry(onDemand).Property(b => b.CreatedAt).CurrentValue = DateTime.UtcNow.AddMinutes(-5);
        await context.SaveChangesAsync();

        var queue = await new BookingRepository(context).GetPendingAssignmentDueForDispatchAsync(
            DateTime.UtcNow.AddMinutes(-60), limit: 10);

        // The 5-hour-old On-Demand booking is excluded despite outranking both: the recency bound
        // is what stops the job hammering bookings nobody is waiting on any more. It used to be
        // applied in memory, after loading every PendingAssignment booking ever created.
        Assert.Equal(new[] { onDemand.Id, regular.Id }, queue.Select(b => b.Id).ToArray());
    }

    [Fact]
    public async Task The_operator_listing_keeps_its_own_newest_first_ordering()
    {
        // Not a dispatch query. Reordering it to suit the pulse job would silently rearrange a
        // back-office screen that never asked for it.
        await using var context = NewContext();
        var customerId = await SeedCustomerAsync(context);

        var older = NewBooking(customerId, DeliveryMode.OnDemand);
        var newer = NewBooking(customerId, DeliveryMode.Pooling);
        context.Bookings.AddRange(older, newer);
        context.Entry(older).Property(b => b.CreatedAt).CurrentValue = DateTime.UtcNow.AddMinutes(-30);
        context.Entry(newer).Property(b => b.CreatedAt).CurrentValue = DateTime.UtcNow;
        await context.SaveChangesAsync();

        var listing = await new BookingRepository(context).GetPendingAssignmentBookingsAsync();

        Assert.Equal(new[] { newer.Id, older.Id }, listing.Select(b => b.Id).ToArray());
    }

    // --- The driver's inbox ---

    [Fact]
    public async Task A_driver_sees_a_higher_ranked_offer_ahead_of_an_older_one()
    {
        await using var context = NewContext();
        var customerId = await SeedCustomerAsync(context);
        var driverId = Guid.NewGuid();

        var regular = Broadcasting(context, customerId, DeliveryMode.Regular);
        var onDemand = Broadcasting(context, customerId, DeliveryMode.OnDemand);
        await context.SaveChangesAsync();

        // The Regular offer is older, so oldest-first ordering would bury the On-Demand one.
        var regularOffer = new DriverBookingOffer(regular.Id, driverId, DateTime.UtcNow.AddMinutes(5));
        var onDemandOffer = new DriverBookingOffer(onDemand.Id, driverId, DateTime.UtcNow.AddMinutes(5));
        context.DriverBookingOffers.AddRange(regularOffer, onDemandOffer);
        context.Entry(regularOffer).Property(o => o.OfferedAt).CurrentValue = DateTime.UtcNow.AddMinutes(-4);
        context.Entry(onDemandOffer).Property(o => o.OfferedAt).CurrentValue = DateTime.UtcNow;
        await context.SaveChangesAsync();

        var inbox = await new DriverBookingOfferRepository(context)
            .GetPendingOffersForDriverAsync(driverId, limit: 3);

        Assert.Equal(
            new[] { onDemandOffer.Id, regularOffer.Id },
            inbox.Select(o => o.Id).ToArray());
    }

    [Fact]
    public async Task Offers_of_equal_rank_stay_oldest_first()
    {
        await using var context = NewContext();
        var customerId = await SeedCustomerAsync(context);
        var driverId = Guid.NewGuid();

        var first = Broadcasting(context, customerId, DeliveryMode.Regular);
        var second = Broadcasting(context, customerId, DeliveryMode.Regular);
        await context.SaveChangesAsync();

        var older = new DriverBookingOffer(first.Id, driverId, DateTime.UtcNow.AddMinutes(5));
        var newer = new DriverBookingOffer(second.Id, driverId, DateTime.UtcNow.AddMinutes(5));
        context.DriverBookingOffers.AddRange(older, newer);
        context.Entry(older).Property(o => o.OfferedAt).CurrentValue = DateTime.UtcNow.AddMinutes(-4);
        context.Entry(newer).Property(o => o.OfferedAt).CurrentValue = DateTime.UtcNow;
        await context.SaveChangesAsync();

        var inbox = await new DriverBookingOfferRepository(context)
            .GetPendingOffersForDriverAsync(driverId, limit: 3);

        Assert.Equal(new[] { older.Id, newer.Id }, inbox.Select(o => o.Id).ToArray());
    }

    [Fact]
    public async Task The_inbox_still_hides_expired_offers_and_other_drivers_work()
    {
        // The join added rank; it must not have widened what the endpoint returns.
        await using var context = NewContext();
        var customerId = await SeedCustomerAsync(context);
        var driverId = Guid.NewGuid();

        var booking = Broadcasting(context, customerId, DeliveryMode.OnDemand);
        var other = Broadcasting(context, customerId, DeliveryMode.OnDemand);
        await context.SaveChangesAsync();

        var expired = new DriverBookingOffer(booking.Id, driverId, DateTime.UtcNow.AddMinutes(-1));
        var someoneElses = new DriverBookingOffer(other.Id, Guid.NewGuid(), DateTime.UtcNow.AddMinutes(5));
        context.DriverBookingOffers.AddRange(expired, someoneElses);
        await context.SaveChangesAsync();

        var inbox = await new DriverBookingOfferRepository(context)
            .GetPendingOffersForDriverAsync(driverId, limit: 3);

        Assert.Empty(inbox);
    }

    [Fact]
    public async Task The_inbox_limit_is_applied_after_ranking()
    {
        await using var context = NewContext();
        var customerId = await SeedCustomerAsync(context);
        var driverId = Guid.NewGuid();

        var pooling = Broadcasting(context, customerId, DeliveryMode.Pooling);
        var onDemand = Broadcasting(context, customerId, DeliveryMode.OnDemand);
        await context.SaveChangesAsync();

        var poolingOffer = new DriverBookingOffer(pooling.Id, driverId, DateTime.UtcNow.AddMinutes(5));
        var onDemandOffer = new DriverBookingOffer(onDemand.Id, driverId, DateTime.UtcNow.AddMinutes(5));
        context.DriverBookingOffers.AddRange(poolingOffer, onDemandOffer);
        context.Entry(poolingOffer).Property(o => o.OfferedAt).CurrentValue = DateTime.UtcNow.AddMinutes(-4);
        context.Entry(onDemandOffer).Property(o => o.OfferedAt).CurrentValue = DateTime.UtcNow;
        await context.SaveChangesAsync();

        var inbox = await new DriverBookingOfferRepository(context)
            .GetPendingOffersForDriverAsync(driverId, limit: 1);

        Assert.Equal(onDemandOffer.Id, Assert.Single(inbox).Id);
    }
}
