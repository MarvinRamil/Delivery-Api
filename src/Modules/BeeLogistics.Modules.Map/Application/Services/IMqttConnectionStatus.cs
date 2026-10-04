namespace BeeLogistics.Modules.Map.Application.Services;

/// <summary>
/// Read-only view of the MQTT subscriber connection status for monitoring and health checks.
/// Updated by <see cref="Infrastructure.Services.MqttLocationSubscriberService"/>.
/// </summary>
public interface IMqttConnectionStatus
{
    /// <summary>Whether the MQTT client is currently connected to the broker.</summary>
    bool IsConnected { get; }

    /// <summary>Topic pattern we are subscribed to (e.g. drivers/+/geo).</summary>
    string? SubscribedTopic { get; }

    /// <summary>UTC when we last successfully connected.</summary>
    DateTime? LastConnectedAt { get; }

    /// <summary>UTC when we last disconnected.</summary>
    DateTime? LastDisconnectedAt { get; }

    /// <summary>Human-readable disconnect reason or exception message.</summary>
    string? LastDisconnectReason { get; }

    /// <summary>Total number of location messages received since startup.</summary>
    long MessagesReceivedCount { get; }

    /// <summary>Broker host we connect to (from config, for display only).</summary>
    string? BrokerHost { get; }
}
