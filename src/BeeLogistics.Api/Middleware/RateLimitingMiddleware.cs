using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using System.Linq;
using System.IO;

namespace BeeLogistics.Api.Middleware;

/// <summary>
/// Rate limiting middleware to prevent abuse and brute force attacks
/// Uses Redis cache if available, otherwise falls back to in-memory cache
/// Industry-standard defaults are hardcoded for zero-config deployment
/// Can be disabled via feature flag: Features:DisableRateLimiting
/// </summary>
public class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IDistributedCache _cache;
    private readonly ILogger<RateLimitingMiddleware> _logger;
    private readonly bool _isDisabled;

    // Industry-standard rate limits (hardcoded for Docker deployment)
    private const int AUTH_LIMIT = 15;          // 15 login/register attempts (more lax for better UX)
    private const int AUTH_WINDOW_SECONDS = 300; // per 5 minutes (OWASP recommendation)
    
    private const int PASSWORD_LIMIT = 3;        // 3 password reset attempts
    private const int PASSWORD_WINDOW_SECONDS = 600; // per 10 minutes
    
    // OTP-specific rate limits (stricter to prevent spam and brute force)
    private const int OTP_SEND_LIMIT = 5;        // 5 OTP requests per email per 15 minutes
    private const int OTP_SEND_WINDOW_SECONDS = 900; // 15 minutes
    private const int OTP_SEND_IP_LIMIT = 10;    // 10 OTP requests per IP per hour
    private const int OTP_SEND_IP_WINDOW_SECONDS = 3600; // 1 hour
    
    private const int OTP_VERIFY_LIMIT = 10;     // 10 verification attempts per email per 10 minutes
    private const int OTP_VERIFY_WINDOW_SECONDS = 600; // 10 minutes
    
    private const int OTP_RESEND_LIMIT = 3;      // 3 resend requests per email per 15 minutes
    private const int OTP_RESEND_WINDOW_SECONDS = 900; // 15 minutes
    
    private const int GENERAL_LIMIT_DEFAULT = 100; // 100 requests per minute (default)
    private const int GENERAL_WINDOW_SECONDS = 60; // per minute

    // Wallet: per-driver limits to prevent abuse and duplicate submissions
    private const int WALLET_TOPUP_LIMIT = 10;        // 10 top-up create requests per 5 minutes per driver
    private const int WALLET_TOPUP_WINDOW_SECONDS = 300;
    private const int WALLET_WITHDRAW_LIMIT = 10;     // 10 withdrawal requests per 5 minutes per driver
    private const int WALLET_WITHDRAW_WINDOW_SECONDS = 300;

    // Driver offers pending: excluded from general limit; high cap so drivers never miss offers
    private const int DRIVER_OFFERS_PENDING_LIMIT_DEFAULT = 600;   // 600/min (e.g. 10 req/s) — multiple devices/NAT
    private const int DRIVER_OFFERS_PENDING_WINDOW_SECONDS = 60;

    // Webhooks (Zammad, Xendit): higher limit than general; still caps abuse
    private const int WEBHOOK_LIMIT = 300;
    private const int WEBHOOK_WINDOW_SECONDS = 60;

    private readonly int _generalLimit;
    private readonly int _driverOffersPendingLimit;

    public RateLimitingMiddleware(
        RequestDelegate next,
        IDistributedCache cache,
        ILogger<RateLimitingMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _cache = cache;
        _logger = logger;
        _generalLimit = configuration.GetValue("RateLimit:GeneralEndpointLimit", GENERAL_LIMIT_DEFAULT);
        _driverOffersPendingLimit = configuration.GetValue("RateLimit:DriverOffersPendingLimit", DRIVER_OFFERS_PENDING_LIMIT_DEFAULT);

        // Check feature flag to disable rate limiting
        _isDisabled = configuration.GetValue<bool>("Features:DisableRateLimiting", false);
        
        if (_isDisabled)
        {
            _logger.LogWarning("Rate limiting is DISABLED via feature flag (Features:DisableRateLimiting)");
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Check if rate limiting is disabled via feature flag
        if (_isDisabled)
        {
            await _next(context);
            return;
        }

        // Skip rate limiting for health checks, Swagger, Scalar, static files
        if (context.Request.Path.StartsWithSegments("/health") ||
            context.Request.Path.StartsWithSegments("/swagger") ||
            context.Request.Path.StartsWithSegments("/scalar") ||
            context.Request.Path.StartsWithSegments("/hangfire") ||
            context.Request.Path.StartsWithSegments("/hubs") ||
            // Synapse pushes one transaction per room event and retries any non-2xx forever,
            // blocking its queue for this appservice. Rate limiting it would throttle the chat
            // archive into a permanent backlog. Authenticated by hs_token instead.
            context.Request.Path.StartsWithSegments("/appservice"))
        {
            await _next(context);
            return;
        }

        var clientId = GetClientIdentifier(context);
        var endpoint = context.Request.Path.Value ?? "/";
        var method = context.Request.Method;

        // Different limits for different endpoint types (pass context for method/auth-aware rules)
        var (prefix, limit, windowSeconds, useOtpContactKey) = GetLimitConfig(endpoint, context, method);
        
        // For OTP endpoints, use email/phone-based rate limiting when provided in the body
        // Also apply IP-based limiting as secondary check
        string cacheKey;
        string? ipCacheKey = null;
        
        if (useOtpContactKey && method == "POST")
        {
            var (contactType, contactValue) = await ExtractOtpContactFromRequestAsync(context);
            if (!string.IsNullOrEmpty(contactValue) && !string.IsNullOrEmpty(contactType))
            {
                cacheKey = $"ratelimit:{prefix}:{contactType}:{contactValue}";
                
                // Also check IP-based limit for OTP send (stricter IP limit)
                if (prefix == "otp-send")
                {
                    ipCacheKey = $"ratelimit:{prefix}:ip:{GetIpAddress(context)}";
                    var ipLimit = OTP_SEND_IP_LIMIT;
                    var ipWindow = OTP_SEND_IP_WINDOW_SECONDS;
                    
                    var ipCount = await GetCurrentCount(ipCacheKey);
                    if (ipCount >= ipLimit)
                    {
                        _logger.LogWarning(
                            "IP rate limit exceeded for OTP send: {ClientId} on {Endpoint}. Count: {Count}/{Limit}",
                            clientId, endpoint, ipCount, ipLimit);
                        
                        context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsJsonAsync(new
                        {
                            success = false,
                            message = "Too many OTP requests from this IP. Please try again later."
                        });
                        return;
                    }
                    
                    await IncrementCounter(ipCacheKey, ipWindow);
                }
            }
            else
            {
                // Fallback to IP-based if contact not found
                cacheKey = $"ratelimit:{prefix}:{clientId}";
            }
        }
        else
        {
            cacheKey = $"ratelimit:{prefix}:{clientId}";
        }

        try
        {
            var currentCount = await GetCurrentCount(cacheKey);
            
            if (currentCount >= limit)
            {
                _logger.LogWarning(
                    "Rate limit exceeded for {ClientId} on {Endpoint} ({Method}). Count: {Count}/{Limit}",
                    clientId, endpoint, method, currentCount, limit);

                context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                context.Response.ContentType = "application/json";
                
                var resetTime = DateTimeOffset.UtcNow.AddSeconds(windowSeconds);
                var minutes = Math.Ceiling(windowSeconds / 60.0);
                
                context.Response.Headers.Append("Retry-After", windowSeconds.ToString());
                context.Response.Headers.Append("X-RateLimit-Limit", limit.ToString());
                context.Response.Headers.Append("X-RateLimit-Remaining", "0");
                context.Response.Headers.Append("X-RateLimit-Reset", resetTime.ToUnixTimeSeconds().ToString());

                // Use integer for comparison to avoid floating-point equality issues
                var minutesInt = (int)minutes;
                var message = prefix.StartsWith("wallet-", StringComparison.OrdinalIgnoreCase)
                    ? $"Too many wallet requests. Please try again in {minutesInt} minute{(minutesInt != 1 ? "s" : "")}."
                    : $"Too many requests. Please try again in {minutesInt} minute{(minutesInt != 1 ? "s" : "")}.";
                var response = new
                {
                    success = false,
                    message,
                    retryAfter = windowSeconds,
                    retryAfterMinutes = minutesInt,
                    resetTime = resetTime.ToUnixTimeSeconds()
                };

                await context.Response.WriteAsync(JsonSerializer.Serialize(response));
                return;
            }

            // Increment counter
            await IncrementCounter(cacheKey, windowSeconds);
            
            // Add rate limit headers
            var remaining = limit - currentCount - 1;
            context.Response.Headers.Append("X-RateLimit-Limit", limit.ToString());
            context.Response.Headers.Append("X-RateLimit-Remaining", Math.Max(0, remaining).ToString());
            context.Response.Headers.Append("X-RateLimit-Reset", 
                DateTimeOffset.UtcNow.AddSeconds(windowSeconds).ToUnixTimeSeconds().ToString());

            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in rate limiting middleware");
            // On error, allow request through (fail open)
            await _next(context);
        }
    }

    private string GetIpAddress(HttpContext context)
    {
        // Try to get IP from Cloudflare header first
        var cfConnectingIp = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        if (!string.IsNullOrEmpty(cfConnectingIp))
        {
            return cfConnectingIp;
        }
        
        // Try X-Forwarded-For header (for generic proxies)
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            return forwardedFor.Split(',')[0].Trim();
        }
        
        // Fall back to connection IP
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private string GetClientIdentifier(HttpContext context)
    {
        // Try to get authenticated user ID first
        var userId = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(userId))
        {
            return $"user:{userId}";
        }

        // Fall back to IP address
        var ipAddress = GetIpAddress(context);
        return $"ip:{ipAddress}";
    }

    private (string prefix, int limit, int windowSeconds, bool useOtpContactKey) GetLimitConfig(string endpoint, HttpContext? context, string method)
    {
        // Webhooks (Zammad, Xendit): 300/min per IP; still rate limited to cap abuse
        if (endpoint.StartsWith("/api/webhooks", StringComparison.OrdinalIgnoreCase))
            return ("webhook", WEBHOOK_LIMIT, WEBHOOK_WINDOW_SECONDS, false);

        // GET /api/driver-offers/pending: excluded from general limit; dedicated higher cap for polling (controller still [Authorize])
        if (method == "GET" && endpoint.StartsWith("/api/driver-offers/pending", StringComparison.OrdinalIgnoreCase))
            return ("driver-offers-pending", _driverOffersPendingLimit, DRIVER_OFFERS_PENDING_WINDOW_SECONDS, false);

        // OTP send endpoint - strict per-email and per-IP limits
        if (endpoint.StartsWith("/api/auth/send-otp", StringComparison.OrdinalIgnoreCase) ||
            endpoint.StartsWith("/api/auth/send-sms-otp", StringComparison.OrdinalIgnoreCase))
        {
            return ("otp-send", OTP_SEND_LIMIT, OTP_SEND_WINDOW_SECONDS, true);
        }

        // OTP verify endpoint - allow more attempts for verification
        if (endpoint.StartsWith("/api/auth/verify-otp", StringComparison.OrdinalIgnoreCase) ||
            endpoint.StartsWith("/api/auth/verify-sms-otp", StringComparison.OrdinalIgnoreCase))
        {
            return ("otp-verify", OTP_VERIFY_LIMIT, OTP_VERIFY_WINDOW_SECONDS, true);
        }

        // OTP resend endpoint - strict limit to prevent spam
        if (endpoint.StartsWith("/api/auth/resend-otp", StringComparison.OrdinalIgnoreCase) ||
            endpoint.StartsWith("/api/auth/resend-sms-otp", StringComparison.OrdinalIgnoreCase))
        {
            return ("otp-resend", OTP_RESEND_LIMIT, OTP_RESEND_WINDOW_SECONDS, true);
        }

        // Stricter limits for authentication endpoints (brute force protection)
        if (endpoint.StartsWith("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
            endpoint.StartsWith("/api/auth/backoffice-login", StringComparison.OrdinalIgnoreCase) ||
            endpoint.StartsWith("/api/auth/register", StringComparison.OrdinalIgnoreCase))
        {
            return ("auth", AUTH_LIMIT, AUTH_WINDOW_SECONDS, false);
        }

        // Rate limit email check endpoint to prevent enumeration (10 checks per 5 minutes)
        if (endpoint.StartsWith("/api/auth/check-email", StringComparison.OrdinalIgnoreCase))
        {
            return ("email-check", 10, AUTH_WINDOW_SECONDS, false);
        }

        // Stricter limits for password reset endpoints
        if (endpoint.Contains("/password", StringComparison.OrdinalIgnoreCase) ||
            endpoint.Contains("/reset", StringComparison.OrdinalIgnoreCase) ||
            endpoint.Contains("/forgot", StringComparison.OrdinalIgnoreCase))
        {
            return ("password", PASSWORD_LIMIT, PASSWORD_WINDOW_SECONDS, false);
        }

        // Wallet top-up create: per-driver (clientId is user when authenticated)
        if (method == "POST" && endpoint.Contains("/wallet/topup/create", StringComparison.OrdinalIgnoreCase))
        {
            return ("wallet-topup", WALLET_TOPUP_LIMIT, WALLET_TOPUP_WINDOW_SECONDS, false);
        }

        // Wallet withdraw: per-driver
        if (method == "POST" && endpoint.Contains("/wallet/withdraw", StringComparison.OrdinalIgnoreCase))
        {
            return ("wallet-withdraw", WALLET_WITHDRAW_LIMIT, WALLET_WITHDRAW_WINDOW_SECONDS, false);
        }

        // Default limits for all other endpoints (from config, e.g. RateLimit:GeneralEndpointLimit)
        return ("general", _generalLimit, GENERAL_WINDOW_SECONDS, false);
    }

    /// <summary>
    /// Extract email or phone from OTP request body for rate limiting (non-blocking).
    /// </summary>
    private async Task<(string? ContactType, string? ContactValue)> ExtractOtpContactFromRequestAsync(HttpContext context)
    {
        try
        {
            if (context.Request.Method != "POST" ||
                !context.Request.Path.Value?.Contains("/otp", StringComparison.OrdinalIgnoreCase) == true)
            {
                return (null, null);
            }

            context.Request.EnableBuffering();

            context.Request.Body.Position = 0;
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true, bufferSize: 1024);
            var body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;

            if (string.IsNullOrWhiteSpace(body))
            {
                return (null, null);
            }

            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("phoneNumber", out var phoneElement))
            {
                var phone = phoneElement.GetString();
                if (!string.IsNullOrWhiteSpace(phone))
                {
                    var digits = new string(phone.Where(char.IsDigit).ToArray());
                    return ("phone", digits);
                }
            }

            if (doc.RootElement.TryGetProperty("email", out var emailElement))
            {
                var email = emailElement.GetString();
                if (!string.IsNullOrWhiteSpace(email))
                {
                    return ("email", email.Trim().ToLowerInvariant());
                }
            }
        }
        catch
        {
            // fallback to IP-based limiting
        }

        return (null, null);
    }

    private async Task<int> GetCurrentCount(string cacheKey)
    {
        try
        {
            var cached = await _cache.GetStringAsync(cacheKey);
            return cached != null && int.TryParse(cached, out var count) ? count : 0;
        }
        catch
        {
            // If cache is unavailable, return 0 to allow request through
            return 0;
        }
    }

    private async Task IncrementCounter(string cacheKey, int windowSeconds)
    {
        try
        {
            var currentCount = await GetCurrentCount(cacheKey);
            var newCount = currentCount + 1;
            
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(windowSeconds)
            };

            await _cache.SetStringAsync(cacheKey, newCount.ToString(), options);
        }
        catch
        {
            // If cache is unavailable, continue without rate limiting
        }
    }
}
