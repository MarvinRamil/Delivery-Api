# Location Service - Integration Guide

## Prerequisites

1. ✅ MQTT broker running on `mqtt.gregdoesdev.xyz:80` (WSS)
2. ✅ RabbitMQ internal instance
3. ✅ Redis instance
4. ✅ PostgreSQL database

---

## Step 1: Add Configuration to `appsettings.json`

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=your-db-host;Database=bee_logistics_db;Username=postgres;Password=your-password"
  },
  
  "Mqtt": {
    "Host": "mqtt.gregdoesdev.xyz",
    "Port": "80",
    "Username": "ilocosscript",
    "Password": "your-mqtt-password",
    "UseWebSocket": "true",
    "LocationTopic": "drivers/+/geo"
  },
  
  "Redis": {
    "ConnectionString": "localhost:6379"
  },
  
  "RabbitMQ": {
    "Host": "localhost",
    "Port": 5672,
    "Username": "guest",
    "Password": "guest"
  }
}
```

---

## Step 2: Update `Program.cs`

Add the following to your `Program.cs`:

```csharp
using BeeLogistics.Modules.Map;
using MassTransit;

var builder = WebApplication.CreateBuilder(args);

// ... existing services ...

// Add Map Module
builder.Services.AddMapModule(builder.Configuration);

// Configure MassTransit with RabbitMQ
builder.Services.AddMassTransit(x =>
{
    // Register location consumers
    x.AddLocationConsumers();

    // Configure RabbitMQ
    x.UsingRabbitMq((context, cfg) =>
    {
        var rabbitHost = builder.Configuration["RabbitMQ:Host"] ?? "localhost";
        var rabbitPort = int.Parse(builder.Configuration["RabbitMQ:Port"] ?? "5672");
        var rabbitUser = builder.Configuration["RabbitMQ:Username"] ?? "guest";
        var rabbitPass = builder.Configuration["RabbitMQ:Password"] ?? "guest";

        cfg.Host(rabbitHost, rabbitPort, "/", h =>
        {
            h.Username(rabbitUser);
            h.Password(rabbitPass);
        });

        // Configure location update endpoint
        cfg.ReceiveEndpoint("driver-location-updates", e =>
        {
            // BURST CONTROL: Only fetch 20 messages at once
            e.PrefetchCount = 20;
            
            // MESSAGE TTL: Discard messages older than 30 seconds
            e.SetQueueArgument("x-message-ttl", 30000);
            
            // RETRY POLICY: Retry 3 times with exponential backoff
            e.UseMessageRetry(r => r.Interval(3, TimeSpan.FromMilliseconds(500)));
            
            // Register consumer
            e.ConfigureConsumer<DriverLocationUpdatedConsumer>(context);
        });

        cfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();

// ... rest of your app configuration ...

app.Run();
```

---

## Step 3: Create Database Migration

Run the following commands:

```powershell
# Navigate to the Map module
cd src/Modules/BeeLogistics.Modules.Map

# Create migration for LocationHistory table
dotnet ef migrations add AddLocationHistoryTable --context MapDbContext --output-dir Migrations

# Apply migration
dotnet ef database update --context MapDbContext
```

---

## Step 4: Update `.env` File

Make sure your `.env` has:

```env
# MQTT Configuration
MQTT_HOST=mqtt.gregdoesdev.xyz
MQTT_PORT=80
MQTT_USERNAME=ilocosscript
MQTT_PASSWORD=your-password-here

# RabbitMQ Configuration
RABBITMQ_HOST=localhost
RABBITMQ_PORT=5672
RABBITMQ_USER=guest
RABBITMQ_PASSWORD=guest

# Redis Configuration
REDIS_CONNECTION_STRING=localhost:6379
```

---

## Step 5: Testing the Service

### Test 1: MQTT Connection

Check logs on startup. You should see:

```
INFO MqttLocationSubscriberService: MQTT Location Subscriber Service starting...
INFO MqttLocationSubscriberService: Connecting to MQTT broker...
INFO MqttLocationSubscriberService: Connected to MQTT broker successfully
INFO MqttLocationSubscriberService: Subscribed to MQTT topic: drivers/+/geo
```

### Test 2: Publish Test Message

Using an MQTT client (like MQTTX):

```
Host: wss://mqtt.gregdoesdev.xyz:80/mqtt
Username: ilocosscript
Password: your-password

Topic: drivers/abc-123-test/geo
Payload:
{
  "Latitude": 14.5995,
  "Longitude": 120.9842,
  "Speed": 45.5,
  "Heading": 120.0,
  "Accuracy": 10.5,
  "Timestamp": "2025-12-29T10:00:00Z"
}
```

### Test 3: Check Redis

```bash
redis-cli

# Check if location was stored
GEOPOS fleet:locations "abc-123-test"
# Should return: 120.9842, 14.5995

# Check metadata
HGETALL driver:meta:abc-123-test
# Should show speed, heading, etc.
```

### Test 4: Check PostgreSQL

```sql
SELECT * FROM map."LocationHistory" 
WHERE "DriverId" = 'abc-123-test'
ORDER BY "Timestamp" DESC 
LIMIT 10;
```

---

## Step 6: Monitoring

### Check Service Health

```csharp
// Add health check endpoint
builder.Services.AddHealthChecks()
    .AddRedis(builder.Configuration["Redis:ConnectionString"]!)
    .AddRabbitMQ(rabbitConnectionString: "amqp://guest:guest@localhost:5672");

app.MapHealthChecks("/health");
```

### Check RabbitMQ Queue

Visit: `http://localhost:15672` (RabbitMQ Management UI)

- Queue: `driver-location-updates`
- Monitor: Message rate, consumer count, queue depth

---

## Troubleshooting

### Issue 1: "Failed to connect to MQTT broker"

**Cause**: MQTT credentials incorrect or broker down

**Solution**:
1. Verify `appsettings.json` has correct `Mqtt:Host`, `Mqtt:Username`, `Mqtt:Password`
2. Test connection manually using MQTTX
3. Check if port 80 is open (not blocked by firewall)

### Issue 2: "Channel buffer full, dropping messages"

**Cause**: Consumer too slow, can't keep up with message rate

**Solution**:
1. Increase channel capacity (change `ChannelCapacity = 1000` to `2000`)
2. Add more consumer instances (horizontal scaling)
3. Check if Redis/SQL is slow (optimize queries)

### Issue 3: "Redis timeout"

**Cause**: Redis overloaded or network issue

**Solution**:
1. Increase Redis memory
2. Check if Redis is running: `redis-cli ping`
3. Increase connection timeout in `RedisLocationCache`

### Issue 4: "Queue depth growing in RabbitMQ"

**Cause**: Consumer crashed or database deadlock

**Solution**:
1. Check consumer logs for errors
2. Increase `PrefetchCount` (fetch more messages per request)
3. Add more consumer workers

---

## Performance Tuning

### For High Traffic (10k+ drivers)

```csharp
// Increase channel capacity
Channel.CreateBounded(5000)

// Increase RabbitMQ prefetch
e.PrefetchCount = 50;

// Increase batch size
private const int BatchSize = 500;  // Was 100
```

### For Low Latency

```csharp
// Reduce batch flush interval
private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);  // Was 30

// Increase retry speed
e.UseMessageRetry(r => r.Interval(3, TimeSpan.FromMilliseconds(100)));  // Was 500
```

---

## Next Steps

1. ✅ **Deploy**: Push to production and monitor
2. ⏳ **SignalR Integration**: Broadcast location updates to customers in real-time
3. ⏳ **Geofencing**: Trigger events when driver enters pickup/dropoff radius
4. ⏳ **Analytics Dashboard**: Fleet owner map showing all active drivers
5. ⏳ **Historical Replay**: Playback driver routes for dispute resolution

---

## Support

For issues or questions:
- Check logs: `docker logs bee-logistics-api`
- RabbitMQ UI: `http://localhost:15672`
- Redis: `redis-cli monitor`
