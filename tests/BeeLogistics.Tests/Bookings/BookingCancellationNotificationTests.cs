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
/// Who hears about a cancellation (issue #47). Cancelling used to publish
/// BookingStatusChanged with <c>SendToAllAsync</c>, which is Clients.All on an [Authorize]d
/// hub — so every signed-in user on the platform received another customer's booking number,
/// canceller id and free-text cancellation reason.
///
/// The audience is the whole point of these tests: the two-parties assertions and the
/// never-broadcast assertion have to hold together, because a fix that is too narrow passes
/// the leak test and silently stops notifying the customer.
/// </summary>
public class BookingCancellationNotificationTests
{
    private readonly IBookingRepository _bookingRepo = Substitute.For<IBookingRepository>();
    private readonly ICustomerRepository _customerRepo = Substitute.For<ICustomerRepository>();
    private readonly IBookingEmailService _emailService = Substitute.For<IBookingEmailService>();
    private readonly IPublishEndpoint _publishEndpoint = Substitute.For<IPublishEndpoint>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly ICustomerIdentityResolver _identityResolver = Substitute.For<ICustomerIdentityResolver>();
    private readonly IFileStorageService _fileStorage = Substitute.For<IFileStorageService>();
    private readonly IBookingAccessPolicy _policy = Substitute.For<IBookingAccessPolicy>();

    public BookingCancellationNotificationTests()
    {
        // Access control is covered by BookingAuthorizationTests. Allow it here, or every
        // assertion below passes vacuously behind a Forbidden.
        _policy.CanAccessAsync(Arg.Any<Booking>(), Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private CancelBookingCommandHandler Handler()
        => new(_bookingRepo, _customerRepo, _emailService, _publishEndpoint, _notifications, _identityResolver,
               Substitute.For<IDirectBusPublisher>(), _fileStorage, _policy,
               NullLogger<CancelBookingCommandHandler>.Instance);

    /// <summary>A cancellable booking owned by a customer whose Identity user id resolves.</summary>
    private Booking CancellableBooking(Guid customerId, string? resolvesTo)
    {
        var booking = new Booking(customerId, "Manila", "Quezon City", "L300", "Boxes", DateTime.UtcNow.AddHours(1));
        _bookingRepo.GetByIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(booking);
        _identityResolver.ResolveIdentityUserIdAsync(customerId, Arg.Any<CancellationToken>()).Returns(resolvesTo);
        return booking;
    }

    private Task<BeeLogistics.Shared.Abstractions.Result<BeeLogistics.Modules.Bookings.Application.DTOs.BookingDto>> Cancel(Booking booking)
        => Handler().Handle(new CancelBookingCommand(booking.Id, "Changed my mind", Guid.NewGuid()), default);

    [Fact]
    public async Task Cancelling_notifies_the_customer_and_the_assigned_driver_and_nobody_else()
    {
        var customerId = Guid.NewGuid();
        var customerIdentityId = Guid.NewGuid().ToString();
        var driverId = Guid.NewGuid();
        var booking = CancellableBooking(customerId, customerIdentityId);
        booking.ConfirmDriver(driverId);

        var result = await Cancel(booking);

        Assert.True(result.IsSuccess);
        await _notifications.Received(1).SendToUserAsync(customerIdentityId, "BookingStatusChanged", Arg.Any<object>());
        await _notifications.Received(1).SendToUserAsync(driverId.ToString(), "BookingStatusChanged", Arg.Any<object>());
        // The bug: this reached every authenticated connection on the platform.
        await _notifications.DidNotReceiveWithAnyArgs().SendToAllAsync(default!, default!);
        await _notifications.Received(2).SendToUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>());
    }

    [Fact]
    public async Task A_booking_with_no_assigned_driver_notifies_only_the_customer()
    {
        var customerId = Guid.NewGuid();
        var customerIdentityId = Guid.NewGuid().ToString();
        var booking = CancellableBooking(customerId, customerIdentityId);

        var result = await Cancel(booking);

        Assert.True(result.IsSuccess);
        await _notifications.Received(1).SendToUserAsync(customerIdentityId, "BookingStatusChanged", Arg.Any<object>());
        await _notifications.Received(1).SendToUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>());
        await _notifications.DidNotReceiveWithAnyArgs().SendToAllAsync(default!, default!);
    }

    [Fact]
    public async Task An_unresolvable_customer_still_gets_notified_via_the_raw_customer_id()
    {
        // Legacy rows stamp the Identity UserId straight into CustomerId, so the resolver finds
        // nothing. Matching update-status, that falls back rather than dropping the notification.
        var customerId = Guid.NewGuid();
        var booking = CancellableBooking(customerId, resolvesTo: null);

        var result = await Cancel(booking);

        Assert.True(result.IsSuccess);
        await _notifications.Received(1).SendToUserAsync(customerId.ToString(), "BookingStatusChanged", Arg.Any<object>());
        await _notifications.DidNotReceiveWithAnyArgs().SendToAllAsync(default!, default!);
    }

    [Fact]
    public async Task The_cancellation_payload_still_carries_the_canceller_and_the_reason()
    {
        // Only the audience changed; the extra cancel-specific fields stay on the wire.
        var customerId = Guid.NewGuid();
        var customerIdentityId = Guid.NewGuid().ToString();
        var canceller = Guid.NewGuid();
        var booking = CancellableBooking(customerId, customerIdentityId);

        await Handler().Handle(new CancelBookingCommand(booking.Id, "Driver too slow", canceller), default);

        var sent = _notifications.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(INotificationService.SendToUserAsync))
            .Select(c => c.GetArguments())
            .Single(a => (string?)a[0] == customerIdentityId)[2];

        Assert.NotNull(sent);
        var fields = sent!.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(sent));
        Assert.Equal(booking.BookingNumber, fields["bookingNumber"]);
        Assert.Equal(canceller, fields["cancelledBy"]);
        Assert.Equal("Driver too slow", fields["cancellationReason"]);
        Assert.Equal(BookingStatus.Cancelled.ToString(), fields["currentStatus"]);
    }
}
