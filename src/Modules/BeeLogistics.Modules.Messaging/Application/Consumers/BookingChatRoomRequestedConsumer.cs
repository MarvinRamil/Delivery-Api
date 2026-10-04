using BeeLogistics.Modules.Messaging.Application.Services;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Messaging.Application.Consumers;

/// <summary>
/// Provisions a booking's chat room when the booking is created.
/// </summary>
/// <remarks>
/// <para>
/// <b>Does not inject <c>IPublishEndpoint</c>, deliberately.</b> The MassTransit EF outbox is bound
/// to <c>BookingsDbContext</c>, so an <c>IPublishEndpoint</c> resolved inside a Messaging consumer
/// would write to an outbox nothing drains and the message would vanish silently. Use <c>IBus</c>
/// or <c>IDirectBusPublisher</c> if this ever needs to publish. A convention test enforces this.
/// </para>
/// <para>
/// Throwing here is correct on transient failures — MassTransit retries (1s/5s/30s, then
/// 1/5/15 min), and the operation is idempotent, so a retry finishes the job rather than
/// duplicating it.
/// </para>
/// </remarks>
public class BookingChatRoomRequestedConsumer : IConsumer<BookingChatRoomRequested>
{
    private readonly IBookingRoomProvisioner _provisioner;
    private readonly ICustomerIdentityResolver _customerIdentity;
    private readonly MatrixOptions _options;
    private readonly ILogger<BookingChatRoomRequestedConsumer> _logger;

    public BookingChatRoomRequestedConsumer(
        IBookingRoomProvisioner provisioner,
        ICustomerIdentityResolver customerIdentity,
        IOptions<MatrixOptions> options,
        ILogger<BookingChatRoomRequestedConsumer> logger)
    {
        _provisioner = provisioner;
        _customerIdentity = customerIdentity;
        _options = options.Value;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<BookingChatRoomRequested> context)
    {
        if (!_options.Enabled)
        {
            // Consuming and discarding beats not registering the consumer: the message is
            // acknowledged rather than piling up in a queue while the feature is off.
            _logger.LogDebug("Matrix disabled; skipping room provisioning for booking {BookingId}",
                context.Message.BookingId);
            return;
        }

        var ct = context.CancellationToken;
        var msg = context.Message;

        // Booking.CustomerId is a Bookings-module Customer.Id, not an Identity user id. Same
        // resolver BookingStatusNotifier uses for SignalR routing.
        var customerUserId = await _customerIdentity.ResolveIdentityUserIdAsync(msg.CustomerId, ct);

        if (string.IsNullOrWhiteSpace(customerUserId))
        {
            // Without an identity there is no Matrix account to seat, and inventing one would
            // create a room the customer could never reach. Throwing puts this through the retry
            // and delayed-redelivery schedule, which covers a customer row that is momentarily
            // unreadable; a permanent failure lands in the error queue where it is visible.
            throw new InvalidOperationException(
                $"Cannot provision a chat room for booking {msg.BookingId}: no Identity user for " +
                $"customer {msg.CustomerId}.");
        }

        await _provisioner.EnsureRoomAsync(
            msg.BookingId,
            msg.BookingNumber,
            customerUserId,
            customerDisplayName: null,
            msg.PickupLocation,
            msg.DropoffLocation,
            ct);
    }
}
