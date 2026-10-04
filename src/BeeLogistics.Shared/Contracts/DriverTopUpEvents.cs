namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Payload sent to drivers when a top-up is paid (webhook processed).
/// Used by SSE stream and SignalR so the app can update wallet and history immediately.
/// </summary>
public sealed record DriverTopUpPaidPayload(
    Guid TopUpId,
    Guid DriverId,
    decimal Amount,
    string Status,
    decimal PersonalBalance,
    decimal TopUpBalance,
    DateTime PaidAtUtc
);

/// <summary>
/// Payload sent to drivers when a top-up payment attempt is declined by the provider.
///
/// The top-up itself is not necessarily dead: the checkout link may still be payable, which is
/// why <see cref="CanRetry"/> is carried explicitly rather than inferred by the client.
/// </summary>
public sealed record DriverTopUpFailedPayload(
    Guid TopUpId,
    Guid DriverId,
    decimal Amount,
    string Reason,
    bool CanRetry,
    string? CheckoutUrl,
    DateTime FailedAtUtc
);

/// <summary>
/// Broadcasts real-time top-up events to connected drivers (SSE and/or SignalR).
/// Implemented in the API host so the Drivers module stays decoupled from transport.
/// </summary>
public interface IDriverTopUpEventBroadcaster
{
    /// <summary>
    /// Notify that a driver's top-up was paid and wallet was credited.
    /// </summary>
    Task PublishPaidAsync(DriverTopUpPaidPayload payload, CancellationToken ct = default);

    /// <summary>
    /// Notify that a top-up payment attempt was declined, so the app can say so immediately
    /// instead of showing a pending state until the record expires hours later.
    /// </summary>
    Task PublishFailedAsync(DriverTopUpFailedPayload payload, CancellationToken ct = default);

    /// <summary>
    /// Subscribe to paid events for a driver (for SSE). Returns a stream that completes when the subscription is disposed or the token is cancelled.
    /// </summary>
    IAsyncEnumerable<DriverTopUpPaidPayload> SubscribeAsync(Guid driverId, CancellationToken ct = default);
}
