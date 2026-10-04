using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Shared.Contracts;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Messaging.Application.Services;

public interface IBookingRoomBackstopSweep
{
    /// <summary>Provisions rooms for recent bookings that somehow have none. Returns how many it fixed.</summary>
    Task<int> SweepAsync(DateTime asOf, CancellationToken ct = default);
}

/// <summary>
/// Catches bookings whose chat room was never created.
/// </summary>
/// <remarks>
/// <para>
/// <b>A backstop, not the live path.</b> <c>BookingChatRoomRequested</c> provisions rooms normally.
/// But the retry budget is finite — 1s/5s/30s, then 1/5/15 min — so a homeserver outage longer than
/// roughly 20 minutes leaves the message in the error queue and that booking permanently without a
/// chat room, with nothing to notice. Given the homeserver is on-prem and production is in the
/// cloud, an outage of that length is a question of when, not if.
/// </para>
/// <para>
/// Same belt-and-braces shape as the payment reconciliation sweep, and for a comparable reason: the
/// thing quietly going missing is a customer's ability to reach their driver.
/// </para>
/// </remarks>
public sealed class BookingRoomBackstopSweep : IBookingRoomBackstopSweep
{
    /// <summary>
    /// How far back to look. Comfortably longer than the full retry-and-redelivery schedule, so a
    /// message still in flight is not treated as lost, but short enough that the sweep stays cheap.
    /// </summary>
    private const int LookbackHours = 24;

    /// <summary>Bounded so a backlog cannot fan one run out into thousands of Synapse calls.</summary>
    private const int BatchSize = 200;

    private readonly IMediator _mediator;
    private readonly IBookingRoomRepository _rooms;
    private readonly IBookingRoomProvisioner _provisioner;
    private readonly ICustomerIdentityResolver _customerIdentity;
    private readonly MatrixOptions _options;
    private readonly ILogger<BookingRoomBackstopSweep> _logger;

    public BookingRoomBackstopSweep(
        IMediator mediator,
        IBookingRoomRepository rooms,
        IBookingRoomProvisioner provisioner,
        ICustomerIdentityResolver customerIdentity,
        IOptions<MatrixOptions> options,
        ILogger<BookingRoomBackstopSweep> logger)
    {
        _mediator = mediator;
        _rooms = rooms;
        _provisioner = provisioner;
        _customerIdentity = customerIdentity;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> SweepAsync(DateTime asOf, CancellationToken ct = default)
    {
        if (!_options.Enabled) return 0;

        var since = asOf.AddHours(-LookbackHours);

        var recent = await _mediator.Send(new GetBookingsCreatedSinceQuery(since, BatchSize), ct);
        if (!recent.IsSuccess || recent.Value is null || recent.Value.Count == 0) return 0;

        var haveRooms = (await _rooms.GetExistingBookingIdsAsync(
            recent.Value.Select(b => b.BookingId).ToList(), ct)).ToHashSet();

        var missing = recent.Value.Where(b => !haveRooms.Contains(b.BookingId)).ToList();
        if (missing.Count == 0) return 0;

        // Loud on purpose. Reaching here at all means the live path failed, and a sweep that
        // quietly papers over a broken pipeline is worse than one that complains.
        _logger.LogWarning(
            "Backstop: {Count} booking(s) since {Since:o} have no chat room. The live provisioning " +
            "path did not complete for them - check the error queue for BookingChatRoomRequested.",
            missing.Count, since);

        var fixedCount = 0;
        foreach (var booking in missing)
        {
            try
            {
                var customerUserId = await _customerIdentity.ResolveIdentityUserIdAsync(booking.CustomerId, ct);
                if (string.IsNullOrWhiteSpace(customerUserId))
                {
                    _logger.LogWarning(
                        "Backstop: booking {BookingId} has no Identity user for customer {CustomerId}; skipping",
                        booking.BookingId, booking.CustomerId);
                    continue;
                }

                await _provisioner.EnsureRoomAsync(
                    booking.BookingId, booking.BookingNumber, customerUserId, null,
                    booking.PickupLocation, booking.DropoffLocation, ct);

                fixedCount++;
            }
            catch (Exception ex)
            {
                // One failure must not abandon the rest; the next run retries it anyway.
                _logger.LogError(ex, "Backstop: could not provision a room for booking {BookingId}", booking.BookingId);
            }
        }

        _logger.LogWarning("Backstop: provisioned {Fixed} of {Missing} missing chat rooms", fixedCount, missing.Count);
        return fixedCount;
    }
}
