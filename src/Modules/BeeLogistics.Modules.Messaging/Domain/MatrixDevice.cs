using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Messaging.Domain;

/// <summary>
/// A Matrix device id issued to one of a user's client installs.
/// </summary>
/// <remarks>
/// <para>
/// <b>No access token is stored here, deliberately.</b> Tokens are minted per request through the
/// appservice login and handed straight to the client; persisting them would turn this table into
/// a credential store worth stealing, for no benefit.
/// </para>
/// <para>
/// The device id is worth remembering, though. Logging in again with the same
/// <c>device_id</c> re-uses the device instead of accumulating a new one per session, which is
/// what stops a driver's account growing an unbounded device list — one row per install, not per
/// app launch.
/// </para>
/// </remarks>
public class MatrixDevice : Entity
{
    private MatrixDevice() { }

    public MatrixDevice(string beeUserId, string platform, string deviceId)
    {
        Id = Guid.NewGuid();
        BeeUserId = beeUserId;
        Platform = platform;
        DeviceId = deviceId;
    }

    public string BeeUserId { get; private set; } = string.Empty;

    /// <summary>Which client this is — e.g. <c>driver-android</c>, <c>web</c>.</summary>
    public string Platform { get; private set; } = string.Empty;

    /// <summary>The Matrix device id Synapse issued.</summary>
    public string DeviceId { get; private set; } = string.Empty;

    public DateTime? LastIssuedAt { get; private set; }

    public void MarkIssued() => LastIssuedAt = DateTime.UtcNow;
}
