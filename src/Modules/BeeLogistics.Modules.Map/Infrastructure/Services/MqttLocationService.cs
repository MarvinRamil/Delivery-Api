using BeeLogistics.Modules.Map.Application.Interfaces;
using MQTTnet;
using MQTTnet.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace BeeLogistics.Modules.Map.Infrastructure.Services;

public class MqttLocationService : IMqttLocationService, IDisposable
{
    private readonly IMqttClient _mqttClient;
    private readonly MqttClientOptions _mqttOptions;
    private readonly ILogger<MqttLocationService> _logger;
    private readonly string _topicPrefix = "beelogistics/drivers/location";
    private bool _disposed = false;

    public MqttLocationService(IConfiguration configuration, ILogger<MqttLocationService> logger)
    {
        _logger = logger;
        
        var mqttHost = configuration["Mqtt:Host"] ?? "localhost";
        var mqttPort = int.Parse(configuration["Mqtt:Port"] ?? "1883");
        var mqttUsername = configuration["Mqtt:Username"];
        var mqttPassword = configuration["Mqtt:Password"];
        var useWebSocket = configuration["Mqtt:UseWebSocket"]?.ToLower() == "true" || mqttPort == 80 || mqttPort == 443;

        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();

        MqttClientOptionsBuilder optionsBuilder;
        
        if (useWebSocket)
        {
            // Use WebSocket connection
            var protocol = mqttPort == 443 ? "wss" : "ws";
            var wsUri = $"{protocol}://{mqttHost}:{mqttPort}/mqtt";
            optionsBuilder = new MqttClientOptionsBuilder()
                .WithWebSocketServer(o => o.WithUri(wsUri))
                .WithClientId($"beelogistics-location-service-{Guid.NewGuid()}")
                .WithCleanSession();
        }
        else
        {
            // Use TCP connection
            optionsBuilder = new MqttClientOptionsBuilder()
                .WithTcpServer(mqttHost, mqttPort)
                .WithClientId($"beelogistics-location-service-{Guid.NewGuid()}")
                .WithCleanSession();
        }

        if (!string.IsNullOrEmpty(mqttUsername) && !string.IsNullOrEmpty(mqttPassword))
        {
            optionsBuilder.WithCredentials(mqttUsername, mqttPassword);
        }

        _mqttOptions = optionsBuilder.Build();

        // Connect to MQTT broker
        _ = ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        try
        {
            await _mqttClient.ConnectAsync(_mqttOptions);
            _logger.LogInformation("MQTT client connected to broker");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to MQTT broker");
        }
    }

    public async Task PublishDriverLocationAsync(
        Guid driverId,
        decimal latitude,
        decimal longitude,
        decimal? speed = null,
        decimal? heading = null,
        CancellationToken ct = default)
    {
        if (!_mqttClient.IsConnected)
        {
            await ConnectAsync();
        }

        if (!_mqttClient.IsConnected)
        {
            _logger.LogWarning("MQTT client not connected, skipping location publish");
            return;
        }

        try
        {
            var topic = $"{_topicPrefix}/{driverId}";
            
            var payload = new
            {
                DriverId = driverId,
                Latitude = latitude,
                Longitude = longitude,
                Speed = speed,
                Heading = heading,
                Timestamp = DateTime.UtcNow
            };

            var messageJson = JsonSerializer.Serialize(payload);
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(messageJson)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _mqttClient.PublishAsync(message, ct);
            _logger.LogDebug("Published location for driver {DriverId} to topic {Topic}", driverId, topic);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish location for driver {DriverId}", driverId);
        }
    }

    public async Task SubscribeToDriverLocationsAsync(
        Func<Guid, decimal, decimal, decimal?, decimal?, Task> onLocationUpdate,
        CancellationToken ct = default)
    {
        if (!_mqttClient.IsConnected)
        {
            await ConnectAsync();
        }

        if (!_mqttClient.IsConnected)
        {
            _logger.LogWarning("MQTT client not connected, cannot subscribe");
            return;
        }

        try
        {
            var topic = $"{_topicPrefix}/+"; // Subscribe to all driver locations
            
            await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                .WithTopic(topic)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(), ct);

            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                try
                {
                    var topicParts = e.ApplicationMessage.Topic.Split('/');
                    if (topicParts.Length >= 4 && Guid.TryParse(topicParts[3].AsSpan(), out var driverId))
                    {
                        var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
                        var locationData = JsonSerializer.Deserialize<LocationPayload>(payload);

                        if (locationData != null)
                        {
                            await onLocationUpdate(
                                driverId,
                                locationData.Latitude,
                                locationData.Longitude,
                                locationData.Speed,
                                locationData.Heading
                            );
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing MQTT location message");
                }
            };

            _logger.LogInformation("Subscribed to driver location updates on topic {Topic}", topic);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to subscribe to driver locations");
        }
    }

    public Task<bool> IsConnectedAsync()
    {
        return Task.FromResult(_mqttClient.IsConnected);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _mqttClient?.DisconnectAsync().GetAwaiter().GetResult();
            _mqttClient?.Dispose();
            _disposed = true;
        }
    }

    private class LocationPayload
    {
        public Guid DriverId { get; set; }
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
        public decimal? Speed { get; set; }
        public decimal? Heading { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
