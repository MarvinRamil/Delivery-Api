using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Handlers;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Notification.Application.Services;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Hubs;
using MassTransit;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// Object-level authorization on the by-id booking endpoints (issue #22). These three
/// handlers previously acted on any booking for any authenticated caller, so the tests that
/// matter most are the cross-user denials - and, for the mutating ones, that a denial leaves
/// the aggregate completely untouched.
///
/// The real <see cref="BookingAccessPolicy"/> is used throughout rather than a stubbed one,
/// so the Customer-entity-Id vs Identity-UserId mapping is exercised for real; only the
/// resolver behind it is faked.
/// </summary>
public class BookingAuthorizationTests
{
    private readonly IBookingRepository _bookingRepo = Substitute.For<IBookingRepository>();
    private readonly ICustomerRepository _customerRepo = Substitute.For<ICustomerRepository>();
    private readonly ICustomerIdentityResolver _identityResolver = Substitute.For<ICustomerIdentityResolver>();
    private readonly IDriverDisplayInfoProvider _driverDisplay = Substitute.For<IDriverDisplayInfoProvider>();
    private readonly IFileStorageService _fileStorage = Substitute.For<IFileStorageService>();
    private readonly IBookingEmailService _emailService = Substitute.For<IBookingEmailService>();
    private readonly IPublishEndpoint _publishEndpoint = Substitute.For<IPublishEndpoint>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();

    private readonly IBookingAccessPolicy _policy;

    public BookingAuthorizationTests()
    {
        _policy = new BookingAccessPolicy(_identityResolver);
    }

    /// <summary>A booking owned by <paramref name="customerId"/>, registered with the repository.</summary>
    private Booking BookingOwnedBy(Guid customerId)
    {
        var booking = new Booking(customerId, "Manila", "Quezon City", "L300", "Boxes", DateTime.UtcNow.AddHours(1));
        _bookingRepo.GetByIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(booking);
        _bookingRepo.GetByIdWithProofOfDeliveriesAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(booking);
        return booking;
    }

    /// <summary>Wires the resolver so <paramref name="userId"/> resolves to Customer row <paramref name="customerId"/>.</summary>
    private void CustomerRowFor(Guid userId, Guid customerId)
        => _identityResolver.ResolveBookingCustomerIdAsync(userId.ToString(), Arg.Any<CancellationToken>())
            .Returns(customerId);

    private GetBookingByIdQueryHandler GetHandler()
        => new(_bookingRepo, _driverDisplay, _fileStorage, _policy);

    private UpdateBookingStatusCommandHandler StatusHandler()
        => new(_bookingRepo, _customerRepo, _emailService, _publishEndpoint, _notifications, _identityResolver, Substitute.For<IDirectBusPublisher>(), _fileStorage, _policy,
               Substitute.For<ILogger<UpdateBookingStatusCommandHandler>>());

    private DeleteBookingCommandHandler DeleteHandler()
        => new(_bookingRepo, _policy);

    private CancelBookingCommandHandler CancelHandler()
        => new(_bookingRepo, _customerRepo, _emailService, _publishEndpoint, _notifications, _identityResolver, Substitute.For<IDirectBusPublisher>(), _fileStorage, _policy,
               Substitute.For<ILogger<CancelBookingCommandHandler>>());

    // ---------------------------------------------------------------- denials

    [Fact]
    public async Task A_stranger_cannot_read_someone_elses_booking()
    {
        var booking = BookingOwnedBy(Guid.NewGuid());
        var stranger = Guid.NewGuid();

        var result = await GetHandler().Handle(new GetBookingByIdQuery(booking.Id, stranger, IsElevated: false), default);

        Assert.True(result.IsFailure);
        Assert.Equal(ResultErrorKind.Forbidden, result.ErrorKind);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task A_stranger_cannot_complete_someone_elses_booking_and_nothing_is_mutated()
    {
        var booking = BookingOwnedBy(Guid.NewGuid());
        booking.ConfirmDriver(Guid.NewGuid());
        var statusBefore = booking.Status;
        var stranger = Guid.NewGuid();

        var result = await StatusHandler().Handle(
            new UpdateBookingStatusCommand(booking.Id, BookingStatus.Completed, stranger, IsElevated: false), default);

        Assert.Equal(ResultErrorKind.Forbidden, result.ErrorKind);
        // The Completed branch is the dangerous one: it settles the fare and credits driver
        // earnings. None of that may happen behind a denial.
        Assert.Equal(statusBefore, booking.Status);
        Assert.Null(booking.FinalFare);
        await _bookingRepo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _publishEndpoint.DidNotReceive().Publish(Arg.Any<BookingCompletedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stranger_cannot_delete_someone_elses_booking_and_the_row_survives()
    {
        var booking = BookingOwnedBy(Guid.NewGuid());
        var stranger = Guid.NewGuid();

        var result = await DeleteHandler().Handle(new DeleteBookingCommand(booking.Id, stranger, IsElevated: false), default);

        Assert.Equal(ResultErrorKind.Forbidden, result.ErrorKind);
        Assert.False(booking.IsDeleted);
        await _bookingRepo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stranger_cannot_cancel_someone_elses_booking()
    {
        var booking = BookingOwnedBy(Guid.NewGuid());
        var stranger = Guid.NewGuid();

        var result = await CancelHandler().Handle(
            new CancelBookingCommand(booking.Id, "Changed my mind", stranger), default);

        Assert.Equal(ResultErrorKind.Forbidden, result.ErrorKind);
        Assert.NotEqual(BookingStatus.Cancelled, booking.Status);
        await _bookingRepo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------- legitimate access

    [Fact]
    public async Task The_owning_customer_can_read_their_booking_via_the_resolved_customer_id()
    {
        // The normal path: Booking.CustomerId is a Customer entity Id, the JWT carries the
        // Identity UserId, and the two only line up through the resolver.
        var customerId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        CustomerRowFor(userId, customerId);
        var booking = BookingOwnedBy(customerId);

        var result = await GetHandler().Handle(new GetBookingByIdQuery(booking.Id, userId, IsElevated: false), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(booking.Id, result.Value!.Id);
    }

    [Fact]
    public async Task The_owning_customer_can_read_a_legacy_booking_stamped_with_their_raw_user_id()
    {
        // Legacy rows created through the CustomerId-supplied booking path store the Identity
        // UserId directly in CustomerId, and the resolver finds no Customer row for them.
        var userId = Guid.NewGuid();
        _identityResolver.ResolveBookingCustomerIdAsync(userId.ToString(), Arg.Any<CancellationToken>())
            .Returns((Guid?)null);
        var booking = BookingOwnedBy(userId);

        var result = await GetHandler().Handle(new GetBookingByIdQuery(booking.Id, userId, IsElevated: false), default);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task The_assigned_driver_can_read_the_booking_they_are_assigned_to()
    {
        var driverId = Guid.NewGuid();
        var booking = BookingOwnedBy(Guid.NewGuid());
        booking.ConfirmDriver(driverId);

        var result = await GetHandler().Handle(new GetBookingByIdQuery(booking.Id, driverId, IsElevated: false), default);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task An_elevated_caller_can_read_any_booking()
    {
        var booking = BookingOwnedBy(Guid.NewGuid());

        var result = await GetHandler().Handle(new GetBookingByIdQuery(booking.Id, Guid.NewGuid(), IsElevated: true), default);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task An_elevated_caller_can_cancel_any_booking()
    {
        // New capability: backoffice previously could not cancel a booking at all, because the
        // cancel handler allowed only the owning customer and the assigned driver.
        var booking = BookingOwnedBy(Guid.NewGuid());

        var result = await CancelHandler().Handle(
            new CancelBookingCommand(booking.Id, "Cancelled by operations", Guid.NewGuid(), IsElevated: true), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
    }

    [Fact]
    public async Task An_elevated_caller_soft_deletes_rather_than_destroying_the_row()
    {
        var booking = BookingOwnedBy(Guid.NewGuid());
        var operatorId = Guid.NewGuid();

        var result = await DeleteHandler().Handle(new DeleteBookingCommand(booking.Id, operatorId, IsElevated: true), default);

        Assert.True(result.IsSuccess);
        Assert.True(booking.IsDeleted);
        Assert.Equal(operatorId.ToString(), booking.DeletedBy);
        _bookingRepo.DidNotReceive().Remove(Arg.Any<Booking>());
        await _bookingRepo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_booking_reports_not_found_rather_than_forbidden()
    {
        _bookingRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Booking?)null);

        var result = await DeleteHandler().Handle(new DeleteBookingCommand(Guid.NewGuid(), Guid.NewGuid(), IsElevated: true), default);

        Assert.Equal(ResultErrorKind.NotFound, result.ErrorKind);
    }
}
