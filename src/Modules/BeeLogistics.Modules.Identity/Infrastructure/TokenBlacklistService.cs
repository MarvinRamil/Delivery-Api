using BeeLogistics.Modules.Identity.Application;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Identity.Infrastructure;

/// <summary>
/// Redis-backed token blacklist service
/// Tokens are stored with TTL matching their remaining lifetime (auto-cleanup)
/// </summary>
public class TokenBlacklistService : ITokenBlacklistService
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<TokenBlacklistService> _logger;
    
    private const string TOKEN_PREFIX = "blacklist:token:";
    private const string USER_PREFIX = "blacklist:user:";
    private static readonly TimeSpan DEFAULT_EXPIRY = TimeSpan.FromMinutes(60); // Match JWT expiry

    public TokenBlacklistService(IDistributedCache cache, ILogger<TokenBlacklistService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task BlacklistTokenAsync(string jti, TimeSpan? expiresIn = null)
    {
        try
        {
            var key = $"{TOKEN_PREFIX}{jti}";
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiresIn ?? DEFAULT_EXPIRY
            };
            
            await _cache.SetStringAsync(key, "1", options);
            _logger.LogInformation("Token {Jti} blacklisted", jti);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to blacklist token {Jti}", jti);
            throw;
        }
    }

    public async Task BlacklistUserTokensAsync(string userId, TimeSpan? expiresIn = null)
    {
        try
        {
            var key = $"{USER_PREFIX}{userId}";
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiresIn ?? DEFAULT_EXPIRY
            };
            
            // Store timestamp - any token issued before this is invalid
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            await _cache.SetStringAsync(key, timestamp, options);
            _logger.LogInformation("All tokens for user {UserId} blacklisted", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to blacklist user tokens for {UserId}", userId);
            throw;
        }
    }

    public async Task<bool> IsTokenBlacklistedAsync(string jti)
    {
        try
        {
            var key = $"{TOKEN_PREFIX}{jti}";
            var value = await _cache.GetStringAsync(key);
            return !string.IsNullOrEmpty(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check token blacklist for {Jti}", jti);
            // Fail open - if cache is down, allow request (defense in depth via other layers)
            return false;
        }
    }

    public async Task<bool> IsUserBlacklistedAsync(string userId, long? tokenIssuedAtUnix = null)
    {
        try
        {
            var key = $"{USER_PREFIX}{userId}";
            var value = await _cache.GetStringAsync(key);
            
            if (string.IsNullOrEmpty(value)) return false;

            // If we have a timestamp and the token has an issued-at time
            if (tokenIssuedAtUnix.HasValue && long.TryParse(value, out var revocationTime))
            {
                // If token was issued BEFORE the revocation time, it is blacklisted
                // If token was issued AFTER the revocation time, it is valid (user logged in after password reset)
                return tokenIssuedAtUnix.Value < revocationTime;
            }

            // Fallback: If we can't verify timestamps, fail open (allow request)
            // This prevents false positives when iat claim is missing or malformed
            // The token will still be validated by JWT signature and expiration checks
            _logger.LogWarning("Cannot verify token blacklist for user {UserId} - missing or invalid iat claim. Allowing request.", userId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check user blacklist for {UserId}", userId);
            return false;
        }
    }
}

