using BeeLogistics.Modules.Bookings.Domain;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// What happens to a booking when the broadcast window expires with no driver accepting
/// (issue #50). The bug being pinned: only AssignmentStatus moved, so Status stayed Pending
/// forever and the customer's app showed the booking still searching for a driver.
///
/// The guard tests matter as much as the happy path - a version of this that is too eager
/// cancels bookings a driver has just accepted, which is far worse than the bug it fixes.
/// </summary>
public class BookingGiveUpTests
{
#pragma warning disable CS0618 // legacy constructor, as used by the other booking tests
    private static Booking PendingBooking() =>
        new(Guid.NewGuid(), "Manila", "Quezon City", "L300", "Boxes", DateTime.UtcNow.AddHours(1));
#pragma warning restore CS0618

    [Fact]
    public void Giving_up_moves_status_not_just_assignment_status()
    {
        var booking = PendingBooking();

        booking.MarkAsRejectedByAllDrivers();

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(BookingAssignmentStatus.RejectedByAllDrivers, booking.AssignmentStatus);
    }

    [Fact]
    public void Giving_up_records_the_reason_the_customer_sees()
    {
        var booking = PendingBooking();

        booking.MarkAsRejectedByAllDrivers();

        Assert.Equal("No driver found", booking.CancellationReason);
        Assert.Equal(Booking.NoDriverFoundReason, booking.CancellationReason);
        Assert.NotNull(booking.CancelledAt);
    }

    [Fact]
    public void Giving_up_is_attributed_to_nobody_so_it_reads_as_a_system_cancellation()
    {
        var booking = PendingBooking();

        booking.MarkAsRejectedByAllDrivers();

        Assert.Null(booking.CancelledBy);
    }

    [Fact]
    public void A_booking_a_driver_already_accepted_is_left_completely_alone()
    {
        var booking = PendingBooking();
        var driverId = Guid.NewGuid();
        booking.ConfirmDriver(driverId);

        booking.MarkAsRejectedByAllDrivers();

        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(BookingAssignmentStatus.AcceptedByDriver, booking.AssignmentStatus);
        Assert.Equal(driverId, booking.SelectedDriverId);
        Assert.Null(booking.CancellationReason);
        Assert.Null(booking.CancelledAt);
    }

    [Fact]
    public void Giving_up_twice_changes_nothing_the_second_time()
    {
        var booking = PendingBooking();

        booking.MarkAsRejectedByAllDrivers();
        var reasonAfterFirst = booking.CancellationReason;
        var cancelledAtAfterFirst = booking.CancelledAt;

        booking.MarkAsRejectedByAllDrivers();

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(reasonAfterFirst, booking.CancellationReason);
        Assert.Equal(cancelledAtAfterFirst, booking.CancelledAt);
    }

    [Fact]
    public void A_customer_cancellation_is_not_overwritten_by_a_later_sweep()
    {
        var booking = PendingBooking();
        var customerId = Guid.NewGuid();
        booking.Cancel("Customer requested cancellation", customerId);

        booking.MarkAsRejectedByAllDrivers();

        Assert.Equal("Customer requested cancellation", booking.CancellationReason);
        Assert.Equal(customerId, booking.CancelledBy);
    }
}
