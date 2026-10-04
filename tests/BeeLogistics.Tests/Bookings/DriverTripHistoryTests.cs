using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Bookings.Infrastructure;
using BeeLogistics.Modules.Bookings.Infrastructure.Repositories;
using BeeLogistics.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// What <see cref="BookingRepository.GetByDriverIdAsync"/> hands back, and what the callers
/// downstream of it are still responsible for filtering themselves (issue #62).
///
/// The query used to carry <c>b.Status != BookingStatus.Cancelled</c>, which made cancelled
/// trips invisible in the driver app's history - the endpoint behind that screen is the only
/// consumer. The customer-side equivalent never filtered, so a cancelled booking stayed visible
/// to the customer who cancelled it and vanished for the driver it was taken from.
///
/// Removing a predicate widens what every caller sees, so the tests here come in two halves:
/// the history query returns cancelled trips, and <see cref="LiveTrackingAuthorizer"/> still
/// refuses to leak a driver's live location through one. The second half is the one that would
/// hurt if it broke, so it runs against the real repository rather than a substitute.
/// </summary>
public class DriverTripHistoryTests
{
    private static BookingsDbContext NewContext()
        => new(new DbContextOptionsBuilder<BookingsDbContext>()
            .UseInMemoryDatabase($"driver-trip-history-{Guid.NewGuid()}")
            .Options);

    /// <summary>
    /// A persisted customer to hang bookings off. Booking.Customer is a required navigation and
    /// the query Includes it, so a booking pointing at a CustomerId with no row behind it is
    /// dropped from the results entirely - the fixture has to be real or every test here passes
    /// for the wrong reason.
    /// </summary>
    private static async Task<Guid> SeedCustomerAsync(BookingsDbContext context)
    {
        var customer = new Customer(
            "Test Customer", $"customer-{Guid.NewGuid():N}@example.com", "Acme", "09170000000", "Manila");
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        return customer.Id;
    }

    /// <summary>A booking the driver accepted and then had cancelled out from under them.</summary>
    private static Booking CancelledTrip(Guid customerId, Guid driverId)
    {
        var booking = NewBooking(customerId);
        booking.ConfirmDriver(driverId);
        booking.Cancel("Customer changed their mind", customerId);
        return booking;
    }

    /// <summary>A booking the driver accepted and drove to completion.</summary>
    private static Booking CompletedTrip(Guid customerId, Guid driverId)
    {
        var booking = NewBooking(customerId);
        booking.ConfirmDriver(driverId);
        booking.MarkPickedUp();
        booking.MarkInTransit();
        foreach (var stop in booking.Stops)
        {
            stop.MarkAsArrived();
            stop.MarkAsCompleted();
        }
        booking.MarkCompleted();
        return booking;
    }

    /// <summary>A booking the driver is part-way through - live work, not history.</summary>
    private static Booking TripInProgress(Guid customerId, Guid driverId)
    {
        var booking = NewBooking(customerId);
        booking.ConfirmDriver(driverId);
        booking.MarkPickedUp();
        booking.MarkInTransit();
        return booking;
    }

#pragma warning disable CS0618 // The multi-stop constructor needs a stop list; this one is what the other booking tests use.
    private static Booking NewBooking(Guid customerId)
        => new(customerId, "Manila", "Quezon City", "L300", "Boxes", DateTime.UtcNow.AddHours(1));
#pragma warning restore CS0618

    /// <summary>
    /// CreatedAt has a protected setter, so ordering assertions have to go through EF rather
    /// than the domain. Without this every booking in a test shares a timestamp and the sort
    /// assertion passes by luck.
    /// </summary>
    private static void CreatedAt(BookingsDbContext context, Booking booking, DateTime when)
        => context.Entry(booking).Property(b => b.CreatedAt).CurrentValue = when;

    // ------------------------------------------------------------------ the history query

    [Fact]
    public async Task A_cancelled_trip_is_returned_to_the_driver_it_was_assigned_to()
    {
        await using var context = NewContext();
        var driverId = Guid.NewGuid();
        var cancelled = CancelledTrip(await SeedCustomerAsync(context), driverId);
        context.Bookings.Add(cancelled);
        await context.SaveChangesAsync();

        var trips = await new BookingRepository(context).GetByDriverIdAsync(driverId);

        var trip = Assert.Single(trips);
        Assert.Equal(cancelled.Id, trip.Id);
        Assert.Equal(BookingStatus.Cancelled, trip.Status);
    }

    [Fact]
    public async Task A_cancelled_trip_keeps_the_detail_the_driver_needs_to_understand_it()
    {
        await using var context = NewContext();
        var driverId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(context);
        context.Bookings.Add(CancelledTrip(customerId, driverId));
        await context.SaveChangesAsync();

        var trip = Assert.Single(await new BookingRepository(context).GetByDriverIdAsync(driverId));

        // Cancelling does not release the assignment - if it did, removing the status filter
        // would not have been enough to make these trips reachable at all.
        Assert.Equal(driverId, trip.SelectedDriverId);
        Assert.Equal(customerId, trip.CancelledBy);
        Assert.Equal("Customer changed their mind", trip.CancellationReason);
        Assert.NotNull(trip.CancelledAt);
    }

    [Fact]
    public async Task Cancelled_and_completed_trips_come_back_together_newest_first()
    {
        await using var context = NewContext();
        var driverId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(context);

        var oldest = CompletedTrip(customerId, driverId);
        var middle = CancelledTrip(customerId, driverId);
        var newest = CompletedTrip(customerId, driverId);
        context.Bookings.AddRange(oldest, middle, newest);
        CreatedAt(context, oldest, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        CreatedAt(context, middle, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        CreatedAt(context, newest, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        await context.SaveChangesAsync();

        var trips = await new BookingRepository(context).GetByDriverIdAsync(driverId);

        Assert.Equal(new[] { newest.Id, middle.Id, oldest.Id }, trips.Select(t => t.Id));
    }

    [Fact]
    public async Task Another_drivers_cancelled_trip_is_not_returned()
    {
        await using var context = NewContext();
        var driverId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(context);
        context.Bookings.AddRange(
            CancelledTrip(customerId, driverId),
            CancelledTrip(customerId, Guid.NewGuid()));
        await context.SaveChangesAsync();

        var trips = await new BookingRepository(context).GetByDriverIdAsync(driverId);

        Assert.Single(trips);
    }

    [Fact]
    public async Task A_booking_cancelled_before_any_driver_took_it_belongs_to_nobodys_history()
    {
        await using var context = NewContext();
        var customerId = await SeedCustomerAsync(context);
        var unassigned = NewBooking(customerId);
        unassigned.Cancel("No driver found", customerId);
        context.Bookings.Add(unassigned);
        await context.SaveChangesAsync();

        var trips = await new BookingRepository(context).GetByDriverIdAsync(Guid.NewGuid());

        Assert.Empty(trips);
    }

    // ------------------------------------------------------- what callers must still filter

    [Fact]
    public async Task A_cancelled_trip_does_not_grant_the_customer_live_location_access()
    {
        await using var context = NewContext();
        var driverId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(context);
        var requesterUserId = Guid.NewGuid();
        context.Bookings.Add(CancelledTrip(customerId, driverId));
        await context.SaveChangesAsync();

        Assert.False(await Authorizer(context, requesterUserId, customerId)
            .CanTrackDriverAsync(requesterUserId, driverId));
    }

    [Fact]
    public async Task A_trip_still_in_progress_does_grant_live_location_access()
    {
        await using var context = NewContext();
        var driverId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(context);
        var requesterUserId = Guid.NewGuid();
        context.Bookings.Add(TripInProgress(customerId, driverId));
        await context.SaveChangesAsync();

        // The mirror of the test above: proves the denial there comes from the booking being
        // cancelled, not from the fixture failing to wire up a trackable booking at all.
        Assert.True(await Authorizer(context, requesterUserId, customerId)
            .CanTrackDriverAsync(requesterUserId, driverId));
    }

    private static LiveTrackingAuthorizer Authorizer(BookingsDbContext context, Guid requesterUserId, Guid customerId)
    {
        var resolver = Substitute.For<ICustomerIdentityResolver>();
        resolver.ResolveBookingCustomerIdAsync(requesterUserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(customerId);
        return new LiveTrackingAuthorizer(new BookingRepository(context), resolver);
    }
}
