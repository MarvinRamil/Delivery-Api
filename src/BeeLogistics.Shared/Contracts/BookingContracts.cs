using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Shared.Contracts;

public record BookingBroadcastRequested(Guid BookingId);

public record CheckDriverQueue(Guid BookingId);

/// <summary>
/// Command to update booking status from other modules (e.g., Operations).
/// This is a cross-module command that can be handled by the Sales module.
/// </summary>
public record UpdateBookingStatusFromDispatchCommand(Guid BookingId, string Status) : IRequest<Result>;

/// <summary>
/// Command to update dispatch status from other modules (e.g., Sales).
/// This is a cross-module command that can be handled by the Operations module.
/// Used when booking status changes to sync dispatch status.
/// </summary>
public record UpdateDispatchStatusFromBookingCommand(Guid BookingId, string Status) : IRequest<Result>;

/// <summary>
/// Cross-module read: which driver (if any) is/was assigned to a booking. Used by the Payment
/// module to cross-check an admin-supplied DriverId on a manual refund against the booking's
/// actual assignment, rather than trusting the request body outright.
/// </summary>
public record GetBookingSelectedDriverIdQuery(Guid BookingId) : IRequest<Guid?>;

/// <summary>
/// Published whenever a booking reaches Cancelled, from either cancellation path.
/// Consumed by the Payment module so a booking that was paid online can be reconciled
/// against its payment without Bookings knowing anything about refunds (GitLab #51).
/// </summary>
/// <remarks>
/// <see cref="CancelledBy"/> is the discriminator that matters. It is null when nobody
/// cancelled this deliberately — the broadcast window expired with no driver accepting,
/// so the booking failed through no fault of the customer's (see #50). A customer or
/// driver cancelling deliberately carries their user id, and may be subject to a
/// cancellation fee, so the two cases must stay distinguishable across the hop.
/// </remarks>
public sealed record BookingCancelledEvent
{
    public required Guid BookingId { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateTime CancelledAt { get; init; }
    public string? CancellationReason { get; init; }

    /// <summary>Null means platform fault: the booking expired unaccepted rather than being cancelled by anyone.</summary>
    public Guid? CancelledBy { get; init; }

    /// <summary>Null when no driver was ever assigned.</summary>
    public Guid? SelectedDriverId { get; init; }
}

/// <summary>
/// The slice of a cancelled booking the Payment module needs to reconcile it against a payment.
/// Deliberately not the full booking: this crosses a module boundary and feeds a batch sweep.
/// </summary>
public record CancelledBookingInfo(
    Guid BookingId,
    Guid? CancelledBy,
    Guid? SelectedDriverId,
    DateTime CancelledAt,
    string? CancellationReason);

/// <summary>
/// Query for bookings cancelled since a cutoff. Handled by the Bookings module, called from
/// Payment's reconciliation sweep — Payment has its own DbContext and schema and cannot join
/// on booking status. Same cross-module pattern as GetActiveVehicleTypesQuery.
/// </summary>
/// <remarks>
/// The sweep is a backstop for <see cref="BookingCancelledEvent"/>, not a replacement for it.
/// The event is the live path; this catches anything the bus dropped. Belt and braces is
/// justified here because the thing going unreconciled is a customer's money.
/// </remarks>
public record GetCancelledBookingsSinceQuery(DateTime SinceUtc, int Limit)
    : IRequest<Result<IReadOnlyList<CancelledBookingInfo>>>;

/// <summary>
/// Published when a booking is created, asking the Messaging module to provision its chat room.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <c>OrderCreatedEvent</c>, which the Fraud module consumes. They fire
/// at the same moment and carry overlapping data, but coupling chat provisioning to a fraud
/// contract would mean neither could change shape without considering the other.
/// </para>
/// <para>
/// <b>Must be published from inside the Bookings module.</b> The MassTransit EF outbox is bound to
/// <c>BookingsDbContext</c> only, so publishing this from anywhere else silently drops it — see the
/// comment on the outbox registration in Program.cs.
/// </para>
/// <para>
/// <see cref="CustomerId"/> is a Bookings-module <c>Customer.Id</c>, <b>not</b> an Identity user id.
/// The consumer resolves it with <c>ICustomerIdentityResolver</c>. No driver is carried because
/// none exists yet: a booking is created unassigned and gets its driver on acceptance.
/// </para>
/// </remarks>
public sealed record BookingChatRoomRequested
{
    public required Guid BookingId { get; init; }
    public required Guid CustomerId { get; init; }

    /// <summary>Human-facing reference; becomes the room name and part of its alias.</summary>
    public required string BookingNumber { get; init; }

    public string? PickupLocation { get; init; }
    public string? DropoffLocation { get; init; }
    public required DateTime CreatedAt { get; init; }
}

/// <summary>
/// Published when a booking gains a driver, asking the Messaging module to seat them in the
/// booking's chat room.
/// </summary>
/// <remarks>
/// <para>
/// A separate event from <see cref="BookingChatRoomRequested"/> because the two happen at genuinely
/// different times: a booking is created unassigned and may wait minutes for a driver, or never get
/// one at all. The room exists from creation with only the customer in it.
/// </para>
/// <para>
/// Fired from <b>both</b> assignment paths — a driver accepting an offer, and a customer picking a
/// driver directly. Missing either one leaves a driver who cannot see the chat.
/// </para>
/// <para>
/// <see cref="DriverId"/> is already an Identity user id (unlike a booking's CustomerId), so it
/// needs no resolution. Publish this <b>before</b> the SaveChanges that commits the assignment: the
/// outbox stages onto BookingsDbContext and flushes on save, so a publish after the last save is
/// staged and then silently discarded.
/// </para>
/// </remarks>
public sealed record BookingChatDriverAssigned
{
    public required Guid BookingId { get; init; }

    /// <summary>The driver's Identity user id — <c>Booking.SelectedDriverId</c>.</summary>
    public required Guid DriverId { get; init; }

    public string? DriverName { get; init; }
    public required DateTime AssignedAt { get; init; }
}

/// <summary>
/// Published when a booking's status changes, so the Messaging module can post a notice into its
/// chat room.
/// </summary>
/// <remarks>
/// <para>
/// Published through <c>IDirectBusPublisher</c>, <b>not</b> the transactional outbox — deliberately,
/// and for a structural reason. Every status-change site notifies its parties <em>after</em> the
/// SaveChanges that commits the change, and the MassTransit EF outbox flushes on save: a message
/// staged after the last save is discarded when the scope disposes. Rather than re-order five
/// handlers around a cosmetic notice, this takes the same trade-off the push pipeline already
/// takes — immediate publish, and a broker outage costs a status line in the transcript.
/// </para>
/// <para>
/// That trade-off is only acceptable because of what is <em>not</em> here: the room itself and the
/// driver joining it go through the outbox, because losing either would leave a booking with no
/// usable chat at all.
/// </para>
/// </remarks>
public sealed record BookingChatStatusChanged
{
    public required Guid BookingId { get; init; }
    public required string Status { get; init; }
    public string? PreviousStatus { get; init; }
    public required DateTime ChangedAt { get; init; }
}

/// <summary>The slice of a booking the Messaging module needs to provision a chat room for it.</summary>
public sealed record BookingForChatRoom(
    Guid BookingId,
    Guid CustomerId,
    string BookingNumber,
    string? PickupLocation,
    string? DropoffLocation,
    DateTime CreatedAt);

/// <summary>
/// Bookings created since a cutoff. Handled by the Bookings module, called from the Messaging
/// module's backstop sweep.
/// </summary>
/// <remarks>
/// <para>
/// The sweep is a backstop for <see cref="BookingChatRoomRequested"/>, not a replacement for it.
/// The event is the live path; this catches what the bus dropped. Same belt-and-braces shape as
/// <see cref="GetCancelledBookingsSinceQuery"/>, and justified for the same kind of reason: the
/// retry budget is finite (1s/5s/30s, then 1/5/15 min), so a homeserver outage that outlasts it
/// leaves the message in the error queue and that booking permanently without a chat room, with
/// nothing to notice.
/// </para>
/// <para>
/// Messaging has its own DbContext and schema and cannot join across to bookings, which is why
/// this crosses the boundary as a query rather than a SQL join.
/// </para>
/// </remarks>
public record GetBookingsCreatedSinceQuery(DateTime SinceUtc, int Limit)
    : IRequest<Result<IReadOnlyList<BookingForChatRoom>>>;
