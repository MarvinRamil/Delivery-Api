using System.Collections.Concurrent;
using BeeLogistics.Modules.Map.Application.Services;

namespace BeeLogistics.Modules.Map.Infrastructure.Services;

/// <summary>
/// Thread-safe snapshot of MQTT connection status for logging and health endpoints.
/// </summary>
public sealed class MqttConnectionStatus : IMqttConnectionStatus
{
    private bool _isConnected;
    private string? _subscribedTopic;
    private DateTime? _lastConnectedAt;
    private DateTime? _lastDisconnectedAt;
    private string? _lastDisconnectReason;
    private string? _brokerHost;
    private long _messagesReceivedCount;

    public bool IsConnected => _isConnected;
    public string? SubscribedTopic => _subscribedTopic;
    public DateTime? LastConnectedAt => _lastConnectedAt;
    public DateTime? LastDisconnectedAt => _lastDisconnectedAt;
    public string? LastDisconnectReason => _lastDisconnectReason;
    public long MessagesReceivedCount => Interlocked.Read(ref _messagesReceivedCount);
    public string? BrokerHost => _brokerHost;

    internal void SetConnected(string? subscribedTopic, string? brokerHost)
    {
        _isConnected = true;
        _subscribedTopic = subscribedTopic;
        _lastConnectedAt = DateTime.UtcNow;
        _brokerHost = brokerHost;
    }

    internal void SetDisconnected(string? reason)
    {
        _isConnected = false;
        _lastDisconnectedAt = DateTime.UtcNow;
        _lastDisconnectReason = reason ?? "Unknown";
    }

    internal void IncrementMessagesReceived()
    {
        Interlocked.Increment(ref _messagesReceivedCount);
    }

    internal void SetBrokerHost(string? host) => _brokerHost = host;
}
