using BeeLogistics.Modules.Bookings.Application.Handlers;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Notification.Application.Services;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Hubs;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// GitLab #51: cancelling a booking used to publish nothing at all, so the Payment module had no
/// way to learn that a booking someone had already paid for was dead. IPublishEndpoint was even
/// injected into the handler and never used.
///
/// Two things are load-bearing here. The event has to carry <c>CancelledBy</c>, because that is
/// what tells Payment whether this was the platform's failure or a customer's choice — and only
/// the former is a candidate for an automatic refund. And it has to be published *before* the
/// save, so the MassTransit EF outbox on BookingsDbContext commits it in the same transaction: a
/// booking that reaches Cancelled with no event emitted is money stranded with nothing watching.
///
/// The other cancellation path — the broadcast window expiring with nobody accepting — sets
/// CancelledBy to null, which BookingGiveUpTests pins at the domain level.
/// </summary>
public class BookingCancellationEventTests
{
    private readonly IBookingRepository _bookingRepo = Substitute.For<IBookingRepository>();
    private readonly ICustomerRepository _customerRepo = Substitute.For<ICustomerRepository>();
    private readonly IBookingEmailService _emailService = Substitute.For<IBookingEmailService>();
    private readonly IPublishEndpoint _publishEndpoint = Substitute.For<IPublishEndpoint>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly ICustomerIdentityResolver _identityResolver = Substitute.For<ICustomerIdentityResolver>();
    private readonly IFileStorageService _fileStorage = Substitute.For<IFileStorageService>();
    private readonly IBookingAccessPolicy _policy = Substitute.For<IBookingAccessPolicy>();

    public BookingCancellationEventTests()
    {
        // Authorization is BookingAuthorizationTests' subject. Allow it, or these assertions pass
        // vacuously behind a Forbidden.
        _policy.CanAccessAsync(Arg.Any<Booking>(), Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private CancelBookingCommandHandler Handler()
        => new(_bookingRepo, _customerRepo, _emailService, _publishEndpoint, _notifications, _identityResolver,
               Substitute.For<IDirectBusPublisher>(), _fileStorage, _policy,
               NullLogger<CancelBookingCommandHandler>.Instance);

#pragma warning disable CS0618 // legacy constructor, as used by the other booking tests
    private Booking CancellableBooking(Guid customerId)
    {
        var booking = new Booking(customerId, "Manila", "Quezon City", "L300", "Boxes", DateTime.UtcNow.AddHours(1));
        _bookingRepo.GetByIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(booking);
        return booking;
    }
#pragma warning restore CS0618

    [Fact]
    public async Task Cancelling_a_booking_announces_it_so_payment_can_react()
    {
        var customerId = Guid.NewGuid();
        var cancellerId = Guid.NewGuid();
        var booking = CancellableBooking(customerId);

        var result = await Handler().Handle(
            new CancelBookingCommand(booking.Id, "Changed my mind", cancellerId), default);

        Assert.True(result.IsSuccess);
        await _publishEndpoint.Received(1).Publish(
            Arg.Is<BookingCancelledEvent>(e =>
                e.BookingId == booking.Id
                && e.CustomerId == customerId
                && e.CancelledBy == cancellerId
                && e.CancellationReason == "Changed my mind"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The driver is carried so an automatic refund can attribute the wallet reversal. It stays
    /// null when nobody was ever assigned, which is the common shape for the #50 case.
    /// </summary>
    [Fact]
    public async Task The_event_carries_the_assigned_driver_when_there_is_one()
    {
        var driverId = Guid.NewGuid();
        var booking = CancellableBooking(Guid.NewGuid());
        booking.ConfirmDriver(driverId);

        await Handler().Handle(new CancelBookingCommand(booking.Id, "Changed my mind", Guid.NewGuid()), default);

        await _publishEndpoint.Received(1).Publish(
            Arg.Is<BookingCancelledEvent>(e => e.SelectedDriverId == driverId), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The outbox invariant. Published after the save, the message would be staged onto a context
    /// that is never saved again and silently dropped — leaving a cancelled booking whose payment
    /// nothing reconciles, which is the bug #51 is about.
    /// </summary>
    [Fact]
    public async Task The_event_is_published_before_the_save_so_the_outbox_commits_it()
    {
        var booking = CancellableBooking(Guid.NewGuid());

        await Handler().Handle(new CancelBookingCommand(booking.Id, "Changed my mind", Guid.NewGuid()), default);

        Received.InOrder(() =>
        {
            _publishEndpoint.Publish(Arg.Any<BookingCancelledEvent>(), Arg.Any<CancellationToken>());
            _bookingRepo.SaveChangesAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task A_booking_that_cannot_be_cancelled_announces_nothing()
    {
        var booking = CancellableBooking(Guid.NewGuid());
        booking.Cancel("Already gone", Guid.NewGuid());

        var result = await Handler().Handle(
            new CancelBookingCommand(booking.Id, "Changed my mind", Guid.NewGuid()), default);

        Assert.False(result.IsSuccess);
        await _publishEndpoint.DidNotReceive().Publish(
            Arg.Any<BookingCancelledEvent>(), Arg.Any<CancellationToken>());
    }
}
