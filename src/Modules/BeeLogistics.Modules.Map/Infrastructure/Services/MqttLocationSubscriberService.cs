using BeeLogistics.Modules.Map.Application.DTOs;
using BeeLogistics.Modules.Map.Application.Services;
using BeeLogistics.Modules.Map.Domain;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace BeeLogistics.Modules.Map.Infrastructure.Services;

/// <summary>
/// High-performance MQTT subscriber using event-driven architecture
/// - Connects to remote MQTT broker via WSS
/// - Uses System.Threading.Channels for zero-copy buffering
/// - Publishes validated events to RabbitMQ via MassTransit
/// - Implements exponential backoff reconnection strategy
/// </summary>
public sealed class MqttLocationSubscriberService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<MqttLocationSubscriberService> _logger;
    private readonly IConfiguration _configuration;
    private readonly Channel<DriverLocationUpdateDto> _locationChannel;
    private readonly IMqttClient _mqttClient;
    private readonly MqttClientOptions _mqttOptions;
    private readonly MqttConnectionStatus _connectionStatus;
    private readonly string _mqttEnvironment;
    private readonly string _defaultLocationTopicPattern;

    private const int ChannelCapacity = 1000; // Bounded channel to prevent memory overflow
    private static readonly TimeSpan HeartbeatLogInterval = TimeSpan.FromMinutes(1);

    public MqttLocationSubscriberService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<MqttLocationSubscriberService> logger,
        IConfiguration configuration,
        MqttConnectionStatus connectionStatus)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _configuration = configuration;
        _connectionStatus = connectionStatus;
        var configuredEnv = _configuration["Mqtt:Environment"] ?? _configuration["ASPNETCORE_ENVIRONMENT"];
        _mqttEnvironment = MqttTopicConvention.NormalizeEnvironment(configuredEnv);
        _defaultLocationTopicPattern = MqttTopicConvention.BuildLocationTopicPattern(_mqttEnvironment);
        _logger.LogInformation("[MQTT] Resolved environment={Environment}, default location topic={TopicPattern}",
            _mqttEnvironment, _defaultLocationTopicPattern);

        // Create bounded channel for backpressure control
        // If buffer fills up, oldest messages are dropped (BoundedChannelFullMode.DropWrite would be alternative)
        _locationChannel = Channel.CreateBounded<DriverLocationUpdateDto>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        // Configure MQTT client
        var mqttHost = _configuration["Mqtt:Host"] ?? throw new InvalidOperationException("Mqtt:Host is required");
        var mqttPort = int.Parse(_configuration["Mqtt:Port"] ?? "80");
        var mqttUsername = _configuration["Mqtt:Username"];
        var mqttPassword = _configuration["Mqtt:Password"];
        
        // Read raw configuration values for debugging
        var mqttUseSslRaw = _configuration["Mqtt:UseSsl"];
        var mqttIgnoreCertRaw = _configuration["Mqtt:IgnoreCertificateErrors"];
        
        // Parse UseSsl: explicitly check for "false" or "0" (case-insensitive), default to true if not set
        var mqttUseSsl = string.IsNullOrWhiteSpace(mqttUseSslRaw) 
            ? true  // Default to true if not set
            : !(mqttUseSslRaw.Equals("false", StringComparison.OrdinalIgnoreCase) 
                || mqttUseSslRaw.Equals("0", StringComparison.OrdinalIgnoreCase)); // "false" or "0" means false, anything else is true
        
        var mqttIgnoreCertificateErrors = !string.IsNullOrWhiteSpace(mqttIgnoreCertRaw) 
            && (mqttIgnoreCertRaw.Equals("true", StringComparison.OrdinalIgnoreCase) 
                || mqttIgnoreCertRaw.Equals("1", StringComparison.OrdinalIgnoreCase));

        _logger.LogInformation("MQTT Configuration: Environment={Environment}, Host={Host}, Port={Port}, UseSsl={UseSsl} (raw: '{UseSslRaw}'), IgnoreCertificateErrors={IgnoreCert} (raw: '{IgnoreCertRaw}')",
            _mqttEnvironment, mqttHost, mqttPort, mqttUseSsl, mqttUseSslRaw ?? "null", mqttIgnoreCertificateErrors, mqttIgnoreCertRaw ?? "null");

        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();

        // Determine protocol based on UseSsl setting
        // Port 443 always uses WSS, otherwise use UseSsl setting
        string protocol;
        if (mqttPort == 443)
        {
            protocol = "wss"; // Port 443 always uses WSS
            _logger.LogWarning("Port 443 detected, forcing WSS protocol regardless of UseSsl setting");
        }
        else
        {
            protocol = mqttUseSsl ? "wss" : "ws";
        }
        
        var wsUri = $"{protocol}://{mqttHost}:{mqttPort}/mqtt";
        _logger.LogInformation("Connecting to MQTT broker using {Protocol} at {Uri} (UseSsl={UseSsl})", protocol, wsUri, mqttUseSsl);

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithWebSocketServer(o =>
            {
                o.WithUri(wsUri);
                
                // Add Origin header for WebSocket connections (some brokers require this)
                if (protocol == "wss")
                {
                    o.WithRequestHeaders(new Dictionary<string, string>
                    {
                        { "Origin", $"https://{mqttHost}" }
                    });
                }
            })
            .WithClientId($"bee-location-subscriber-{Environment.MachineName}-{Guid.NewGuid():N}")
            .WithCredentials(mqttUsername, mqttPassword)
            .WithCleanSession(false) // Persistent session for reliability
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(TimeSpan.FromSeconds(10));

        // Configure TLS options for WebSocket SSL connections
        // Note: For WebSocket, TLS is handled automatically by the wss:// protocol
        // Certificate validation can be configured here if needed
        // IMPORTANT: WithTlsOptions may not work for WebSocket connections in MQTTnet
        // If certificate validation fails, try setting UseSsl: false to use plain WebSocket
        if (protocol == "wss")
        {
            if (mqttIgnoreCertificateErrors)
            {
                _logger.LogWarning("Ignoring SSL certificate errors for {Host}. This should only be used in development or with trusted certificates.", mqttHost);
                optionsBuilder.WithTlsOptions(o =>
                {
                    // Configure certificate validation to accept all certificates
                    o.WithCertificateValidationHandler(_ =>
                    {
                        _logger.LogDebug("Accepting SSL certificate for {Host} (validation disabled)", mqttHost);
                        return true; // Accept all certificates
                    });
                });
            }
            else
            {
                _logger.LogInformation("Using default SSL certificate validation for {Host}", mqttHost);
            }
        }

        _mqttOptions = optionsBuilder.Build();

        _connectionStatus.SetBrokerHost(mqttHost);

        // Setup event handlers
        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
        _mqttClient.ConnectedAsync += OnConnectedAsync;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MQTT Location Subscriber Service starting...");

        // Start two concurrent tasks:
        // 1. MQTT connection manager
        // 2. Channel processor (consumes from channel, publishes to RabbitMQ)
        var connectionTask = MaintainConnectionAsync(stoppingToken);
        var processingTask = ProcessLocationUpdatesAsync(stoppingToken);

        await Task.WhenAll(connectionTask, processingTask);

        _logger.LogInformation("MQTT Location Subscriber Service stopped");
    }

    private async Task MaintainConnectionAsync(CancellationToken ct)
    {
        var reconnectDelay = TimeSpan.FromSeconds(5);
        const int MaxRetryDelay = 60;
        var lastHeartbeatLog = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_mqttClient.IsConnected)
                {
                    _logger.LogInformation("[MQTT] Connecting to broker at {Host}...", _connectionStatus.BrokerHost ?? "unknown");
                    await _mqttClient.ConnectAsync(_mqttOptions, ct);
                    reconnectDelay = TimeSpan.FromSeconds(5);
                }
                else
                {
                    // Periodic heartbeat log so we can see in app logs that MQTT is still connected
                    var now = DateTime.UtcNow;
                    if (now - lastHeartbeatLog >= HeartbeatLogInterval)
                    {
                        _logger.LogInformation(
                            "[MQTT] Heartbeat: connected to {Host}, subscribed to {Topic}, messages received so far: {Count}",
                            _connectionStatus.BrokerHost,
                            _connectionStatus.SubscribedTopic ?? "?",
                            _connectionStatus.MessagesReceivedCount);
                        lastHeartbeatLog = now;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _connectionStatus.SetDisconnected(ex.Message);
                _logger.LogError(ex, "[MQTT] Failed to connect to broker. Retrying in {Delay}s...", reconnectDelay.TotalSeconds);
                await Task.Delay(reconnectDelay, ct);
                reconnectDelay = TimeSpan.FromSeconds(Math.Min(reconnectDelay.TotalSeconds * 2, MaxRetryDelay));
            }
        }
    }

    private async Task OnConnectedAsync(MqttClientConnectedEventArgs args)
    {
        _logger.LogInformation("[MQTT] Connected to broker successfully (ServerId={ServerId})", args.ConnectResult?.ServerReference ?? "n/a");

        try
        {
            var topic = _configuration["Mqtt:LocationTopic"] ?? _defaultLocationTopicPattern;
            _logger.LogInformation("[MQTT] Subscribing with environment guard. Environment={Environment}, topic={Topic}",
                _mqttEnvironment, topic);
            await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                .WithTopic(topic)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                .Build());

            _connectionStatus.SetConnected(topic, _connectionStatus.BrokerHost);
            _logger.LogInformation("[MQTT] Subscribed to topic: {Topic}", topic);
        }
        catch (Exception ex)
        {
            _connectionStatus.SetDisconnected(ex.Message);
            _logger.LogError(ex, "[MQTT] Failed to subscribe to topic");
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        var reason = args.Exception?.Message ?? args.Reason.ToString();
        _connectionStatus.SetDisconnected(reason);

        if (args.Exception != null)
        {
            _logger.LogWarning(args.Exception, "[MQTT] Disconnected from broker. Reason={Reason}, ClientWasConnected={ClientWasConnected}",
                args.Reason, args.ClientWasConnected);
        }
        else
        {
            _logger.LogInformation("[MQTT] Disconnected from broker. Reason={Reason}, ClientWasConnected={ClientWasConnected}",
                args.Reason, args.ClientWasConnected);
        }

        return Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        _logger.LogInformation("[MQTT] Receiving message on topic {Topic}", topic);

        try
        {
            // Fast-path: Parse topic to extract driverId
            // Topic formats:
            // - prod: beelogistics/drivers/{driverId}/location
            // - non-prod: {env}/beelogistics/drivers/{driverId}/location
            var topicParts = args.ApplicationMessage.Topic.Split('/');
            Guid driverId;
            
            if (_mqttEnvironment == "prod")
            {
                if (!(topicParts.Length >= 4
                    && topicParts[0] == "beelogistics"
                    && topicParts[1] == "drivers"
                    && Guid.TryParse(topicParts[2].AsSpan(), out driverId)
                    && topicParts[3] == "location"))
                {
                    _logger.LogWarning("Rejected topic in prod environment: {Topic}", args.ApplicationMessage.Topic);
                    return;
                }
            }
            else
            {
                if (!(topicParts.Length >= 5
                    && topicParts[0] == _mqttEnvironment
                    && topicParts[1] == "beelogistics"
                    && topicParts[2] == "drivers"
                    && Guid.TryParse(topicParts[3].AsSpan(), out driverId)
                    && topicParts[4] == "location"))
                {
                    _logger.LogWarning("Rejected topic for environment {Environment}: {Topic}", _mqttEnvironment, args.ApplicationMessage.Topic);
                    return;
                }
            }

            // Deserialize payload
            var payloadJson = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);
            var locationUpdate = JsonSerializer.Deserialize<DriverLocationUpdateDto>(payloadJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (locationUpdate == null)
            {
                _logger.LogWarning("Failed to deserialize location update from topic: {Topic}", args.ApplicationMessage.Topic);
                return;
            }

            // Write to channel (non-blocking, bounded)
            // If channel is full, oldest message is dropped (configured in ctor)
            //
            // Sanitization used to happen here, before the channel. It now runs in the channel
            // processor via ILocationIngestService, shared with the HTTP ingest path. That also
            // fixes a latent race: this handler can run concurrently for different messages, and
            // the sanitizer keeps per-driver rate-limit and teleport state in dictionaries it
            // mutates. Running it on the single-threaded processor serialises those updates.
            await _locationChannel.Writer.WriteAsync(locationUpdate with { DriverId = driverId });

            _connectionStatus.IncrementMessagesReceived();
            _logger.LogInformation("[MQTT] Received and queued location for driver {DriverId} (topic: {Topic})", driverId, topic);
        }
        catch (Exception ex)
        {
            // Catch all exceptions to prevent crashing the MQTT client
            _logger.LogError(ex, "[MQTT] Error processing message from topic {Topic}", topic);
        }
    }

    private async Task ProcessLocationUpdatesAsync(CancellationToken ct)
    {
        _logger.LogInformation("Location update processor started");

        await foreach (var locationUpdate in _locationChannel.Reader.ReadAllAsync(ct))
        {
            try
            {
                // Sanitize and publish through the shared ingest pipeline - the same one the HTTP
                // endpoints use, so both transports get identical validation and both publish
                // through IBus rather than the outbox-backed scoped IPublishEndpoint.
                using var scope = _serviceScopeFactory.CreateScope();
                var ingestService = scope.ServiceProvider.GetRequiredService<ILocationIngestService>();

                await ingestService.IngestAsync(
                    locationUpdate.DriverId,
                    locationUpdate,
                    LocationSource.Mqtt,
                    isHistorical: false,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish location event for driver {DriverId}", locationUpdate.DriverId);
                // Continue processing remaining messages
            }
        }

        _logger.LogInformation("Location update processor stopped");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping MQTT Location Subscriber Service...");

        // Complete the channel to signal no more writes
        _locationChannel.Writer.Complete();

        // Disconnect MQTT client gracefully
        if (_mqttClient.IsConnected)
        {
            await _mqttClient.DisconnectAsync(cancellationToken: cancellationToken);
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _mqttClient?.Dispose();
        base.Dispose();
    }
}
