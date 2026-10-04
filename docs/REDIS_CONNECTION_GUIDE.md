# Redis Connection Guide

This document explains how Redis connections are configured and used in the BeeLogistics application.

## Overview

The application uses **two separate Redis connections** for different purposes:

1. **Main Redis Cache** - For distributed caching, token blacklisting, OTP storage, and rate limiting
2. **Map Module Redis** - For geospatial operations (driver locations, geofencing)

---

## 1. Main Redis Cache Connection

### Configuration Source

Redis connection string is read from:
- **Configuration Key**: `ConnectionStrings:Redis`
- **Sources** (in priority order):
  1. Environment variable: `ConnectionStrings__Redis`
  2. `appsettings.json`: `"ConnectionStrings": { "Redis": "..." }`
  3. `appsettings.{Environment}.json`

### Current Configuration

**In `appsettings.json`:**
```json
{
  "ConnectionStrings": {
    "Redis": "100.105.242.96"
  }
}
```

**In Docker Compose (`compose.dev.yml`):**
```yaml
environment:
  # Without password (current)
  ConnectionStrings__Redis: redis:6379
  
  # With password (if Redis service has password)
  # ConnectionStrings__Redis: redis:6379,password=yourpassword
```

### Connection Format

The connection string can be:
- **Simple format**: `hostname` or `hostname:port` (default port: 6379)
  - Example: `100.105.242.96` → connects to `100.105.242.96:6379` (no password)
  - Example: `localhost:6379` → connects to `localhost:6379` (no password)
- **With password**: `hostname:port,password=yourpassword`
  - Example: `100.105.242.96:6379,password=mypassword`
  - Example: `redis.example.com:6379,password=securepass123`
- **Full format**: `hostname:port,password=xxx,ssl=true`
  - Example: `redis.example.com:6380,password=mypassword,ssl=true`
  - Example: `redis.example.com:6380,password=mypassword,ssl=true,abortConnect=false`

**Current Configuration**: `"Redis": "100.105.242.96"` - **NO PASSWORD** configured

### How It Works

**Location**: `src/BeeLogistics.Api/Program.cs` (lines 87-114)

```csharp
// 1. Read connection string from configuration
var redisConnection = builder.Configuration.GetConnectionString("Redis");

// 2. If connection string exists, configure Redis
if (!string.IsNullOrEmpty(redisConnection))
{
    try
    {
        // 3. Register StackExchange.Redis as distributed cache
        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnection;  // Connection string
            options.InstanceName = "BeeLogistics:";   // Key prefix
        });
        
        // 4. Register custom cache service
        builder.Services.AddSingleton<ICacheService, RedisCacheService>();
    }
    catch (Exception ex)
    {
        // 5. Fallback to in-memory cache if Redis fails
        Log.Warning(ex, "Failed to configure Redis, falling back to in-memory cache");
        builder.Services.AddDistributedMemoryCache();
    }
}
else
{
    // 6. No Redis configured - use in-memory cache
    builder.Services.AddDistributedMemoryCache();
}
```

### Connection Behavior

- **Lazy Connection**: Redis connection is established on first use, not at startup
- **No Startup Failure**: If Redis is unavailable, the app falls back to in-memory cache
- **Timeout**: Default timeout is 5 seconds (StackExchange.Redis default)
- **Retry**: Automatic reconnection on connection loss

### Usage

The main Redis cache is used by:

1. **Token Blacklist Service** (`TokenBlacklistService`)
   - Stores blacklisted JWT tokens
   - Stores user-level token revocation timestamps
   - Keys: `BeeLogistics:blacklist:token:{jti}`, `BeeLogistics:blacklist:user:{userId}`

2. **OTP Service** (`OtpService`)
   - Stores OTP codes for email verification
   - Keys: `BeeLogistics:otp:{email}`

3. **Rate Limiting Middleware** (`RateLimitingMiddleware`)
   - Tracks API request counts per IP/user
   - Keys: `BeeLogistics:ratelimit:{endpoint}:{identifier}`

4. **General Caching** (`RedisCacheService`)
   - Any service using `IDistributedCache` or `ICacheService`

---

## 2. Map Module Redis Connection

### Configuration Source

Redis connection string is read from:
- **Configuration Keys** (in priority order):
  1. `Redis:ConnectionString` (environment variable: `Redis__ConnectionString`)
  2. `ConnectionStrings:Redis` (environment variable: `ConnectionStrings__Redis`) — the same
     key `Program.cs` uses for the distributed cache, so configuring one key keeps every
     Redis consumer on the same host
  3. If neither is set:
     - **Development**: falls back to `"localhost:6379"` (local convenience)
     - **Any other environment**: throws at startup — no silent localhost fallback

> Note: Vault (`bee/config`) is the highest-precedence configuration source. A
> `Redis__ConnectionString` or `ConnectionStrings__Redis` key stored in Vault overrides
> the values passed as container environment variables.

### How It Works

**Location**: `src/Modules/BeeLogistics.Modules.Map/DependencyInjection.cs`

```csharp
// 1. Read connection string: Redis:ConnectionString ?? ConnectionStrings:Redis
var redisConnectionString = configuration["Redis:ConnectionString"]
    ?? configuration.GetConnectionString("Redis");

// 2. Register ConnectionMultiplexer (direct Redis client)
services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    // 3. Missing config: localhost fallback in Development only; otherwise throw.
    //    The resolved endpoint is logged at first resolution.

    // 4. Parse connection string
    var config = ConfigurationOptions.Parse(connectionString);

    // 5. Configure connection resilience
    config.AbortOnConnectFail = false;  // Don't fail startup if Redis is down
    config.ConnectRetry = 5;             // Retry 5 times
    config.ConnectTimeout = 10000;      // 10 second timeout

    // 6. Create and return connection multiplexer
    return ConnectionMultiplexer.Connect(config);
});
```

### Connection Behavior

- **Resilient Configuration**: 
  - `AbortOnConnectFail = false` - App starts even if Redis is down
  - `ConnectRetry = 5` - Retries 5 times before giving up
  - `ConnectTimeout = 10000` - 10 second connection timeout
- **Direct Connection**: Uses `IConnectionMultiplexer` for advanced Redis features (geospatial commands)

### Usage

The Map module Redis is used for:

1. **Geospatial Operations**
   - Storing driver locations (GEOADD, GEORADIUS)
   - Finding nearby drivers
   - Geofence detection

2. **Location Caching**
   - Caching recent driver locations
   - Reducing database queries

---

## Connection Flow Diagram

```
Application Startup
       │
       ├─> Read Configuration
       │   ├─> Environment Variables (highest priority)
       │   ├─> appsettings.json
       │   └─> Defaults
       │
       ├─> Main Redis Cache
       │   ├─> ConnectionString: "ConnectionStrings:Redis"
       │   ├─> Service: AddStackExchangeRedisCache()
       │   ├─> Used by: TokenBlacklist, OTP, RateLimiting, General Cache
       │   └─> Fallback: In-Memory Cache (if Redis unavailable)
       │
       └─> Map Module Redis
           ├─> ConnectionString: "Redis:ConnectionString"
           ├─> Service: IConnectionMultiplexer
           ├─> Used by: Geospatial operations, Location caching
           └─> Configuration: AbortOnConnectFail=false, Retry=5, Timeout=10s
```

---

## Current Issue: Connection Timeout

### Problem

The application is trying to connect to Redis at `100.105.242.96:6379` but:
- Connection attempts are timing out after 5 seconds
- The server may be down, unreachable, or blocked by firewall
- Errors are logged but the app continues (fail-open behavior)

### Error Message

```
StackExchange.Redis.RedisConnectionException: 
UnableToConnect on 100.105.242.96:6379/Interactive
The message timed out in the backlog attempting to send 
because no connection became available (5000ms)
```

### Solutions

#### Option 1: Use Local Redis (Recommended for Development)

**If using Docker Compose:**
```yaml
# In compose.dev.yml, Redis is already configured
ConnectionStrings__Redis: redis:6379
```

**If running locally:**
1. Start Redis locally:
   ```bash
   docker run -d -p 6379:6379 redis:7-alpine
   ```

2. Update `appsettings.json`:
   ```json
   {
     "ConnectionStrings": {
       "Redis": "localhost:6379"
     }
   }
   ```

#### Option 2: Fix Remote Redis Connection

1. **Test if Redis requires password:**
   ```bash
   # Test without password
   redis-cli -h 100.105.242.96 -p 6379 ping
   # If returns: (error) NOAUTH Authentication required
   # Then Redis requires a password
   
   # Test with password (if you know it)
   redis-cli -h 100.105.242.96 -p 6379 -a yourpassword ping
   # Should return: PONG
   ```

2. **Check firewall/network:**
   - Ensure port 6379 is open
   - Verify IP address is correct
   - Test connectivity: `telnet 100.105.242.96 6379`

3. **Update connection string with password (if required):**
   
   **In `appsettings.json`:**
   ```json
   {
     "ConnectionStrings": {
       "Redis": "100.105.242.96:6379,password=yourpassword"
     }
   }
   ```
   
   **Or via environment variable:**
   ```bash
   # Windows PowerShell
   $env:ConnectionStrings__Redis="100.105.242.96:6379,password=yourpassword"
   
   # Linux/Mac
   export ConnectionStrings__Redis="100.105.242.96:6379,password=yourpassword"
   ```
   
   **In Docker Compose:**
   ```yaml
   environment:
     ConnectionStrings__Redis: "100.105.242.96:6379,password=yourpassword"
   ```

#### Option 3: Disable Redis (Use In-Memory Cache)

**Remove or comment out Redis connection:**
```json
{
  "ConnectionStrings": {
    // "Redis": "100.105.242.96"  // Commented out
  }
}
```

**Or set empty environment variable:**
```bash
# Windows PowerShell
$env:ConnectionStrings__Redis=""

# Linux/Mac
export ConnectionStrings__Redis=""
```

**Note**: In-memory cache works but:
- ❌ Not shared across multiple app instances
- ❌ Data lost on app restart
- ❌ No persistence
- ✅ Works for single-instance development

---

## Testing Redis Connection

### From Application

Check application logs on startup:
```
[INFO] Configuring Redis cache: 100.105.242.96
[INFO] Redis cache configured successfully
```

If Redis is unavailable:
```
[WARN] Failed to configure Redis cache, falling back to in-memory cache
```

### From Command Line

**Using redis-cli:**
```bash
# Test connection
redis-cli -h 100.105.242.96 -p 6379 ping
# Should return: PONG

# Test with password
redis-cli -h 100.105.242.96 -p 6379 -a yourpassword ping
```

**Using telnet:**
```bash
telnet 100.105.242.96 6379
# If connection succeeds, you'll see Redis prompt
```

**Using PowerShell (Windows):**
```powershell
Test-NetConnection -ComputerName 100.105.242.96 -Port 6379
```

---

## Configuration Examples

### Development (Local Redis)

**appsettings.Development.json:**
```json
{
  "ConnectionStrings": {
    "Redis": "localhost:6379"
  },
  "Redis": {
    "ConnectionString": "localhost:6379"
  }
}
```

### Development (Docker Compose)

**compose.dev.yml:**
```yaml
environment:
  ConnectionStrings__Redis: redis:6379
  Redis__ConnectionString: redis:6379
```

### Production (Remote Redis with Password)

**Environment Variables:**
```bash
ConnectionStrings__Redis=redis.production.com:6379,password=securepassword,ssl=true
Redis__ConnectionString=redis.production.com:6379,password=securepassword,ssl=true
```

### Production (Redis Cluster)

**Environment Variables:**
```bash
ConnectionStrings__Redis=redis1:6379,redis2:6379,redis3:6379,password=securepassword
```

---

## Troubleshooting

### Issue: "UnableToConnect" errors

**Causes:**
- Redis server is down
- Network/firewall blocking connection
- Wrong IP address or port
- Redis requires password but not provided

**Solutions:**
1. Verify Redis is running: `redis-cli ping`
2. Check network connectivity: `telnet <host> <port>`
3. Verify connection string format
4. Check firewall rules

### Issue: "Timeout" errors

**Causes:**
- Redis server is slow or overloaded
- Network latency
- Default 5-second timeout too short

**Solutions:**
1. Increase timeout in connection string: `host:port,connectTimeout=10000`
2. Check Redis server performance
3. Verify network quality

### Issue: "Authentication failed" or "NOAUTH Authentication required"

**Causes:**
- Redis requires password but not provided
- Wrong password
- Password contains special characters that need escaping

**Solutions:**
1. **Add password to connection string:**
   ```json
   {
     "ConnectionStrings": {
       "Redis": "100.105.242.96:6379,password=yourpassword"
     }
   }
   ```

2. **If password has special characters, URL encode them:**
   - `@` → `%40`
   - `#` → `%23`
   - `$` → `%24`
   - `%` → `%25`
   - Example: `password=p@ssw0rd#123` → `password=p%40ssw0rd%23123`

3. **Verify password is correct:**
   ```bash
   redis-cli -h 100.105.242.96 -p 6379 -a yourpassword ping
   ```

4. **Check Redis configuration:**
   - Check if `requirepass` is set in Redis config
   - For Docker Redis: `docker exec -it beeapp-redis-dev redis-cli CONFIG GET requirepass`

---

## Best Practices

1. **Use Environment Variables** for sensitive data (passwords, production URLs)
2. **Use Docker Compose** for local development (consistent setup)
3. **Monitor Connection Health** - Check logs for connection issues
4. **Configure Timeouts** appropriately for your network
5. **Use Redis Sentinel/Cluster** for production high availability
6. **Enable SSL** for production Redis connections
7. **Set Appropriate TTLs** for cached data to prevent memory bloat

---

## Summary

- **Two Redis connections**: Main cache (distributed) and Map module (geospatial)
- **Configuration**: Via `ConnectionStrings:Redis` and `Redis:ConnectionString`
- **Resilient**: App continues even if Redis is unavailable (falls back to in-memory)
- **Current Configuration**: **NO PASSWORD** configured - `"Redis": "100.105.242.96"`
- **Current Issue**: Cannot connect to `100.105.242.96:6379` 
  - **Possible causes**: 
    - Redis server is down/unreachable
    - **Redis requires password but none provided** ⚠️
    - Firewall blocking connection
  - **Solution**: Test if password is required, then add to connection string

## Quick Password Check

To check if your Redis requires a password:

```bash
# Test connection without password
redis-cli -h 100.105.242.96 -p 6379 ping

# If you get: (error) NOAUTH Authentication required
# Then add password to connection string:
# "Redis": "100.105.242.96:6379,password=yourpassword"
```

