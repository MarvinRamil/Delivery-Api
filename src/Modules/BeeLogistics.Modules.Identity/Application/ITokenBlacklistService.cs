namespace BeeLogistics.Modules.Identity.Application;

/// <summary>
/// Service for blacklisting JWT tokens (immediate revocation)
/// Uses Redis/distributed cache with automatic expiry matching token lifetime
/// </summary>
public interface ITokenBlacklistService
{
    /// <summary>
    /// Blacklist a specific token by its JTI (JWT ID)
    /// </summary>
    Task BlacklistTokenAsync(string jti, TimeSpan? expiresIn = null);
    
    /// <summary>
    /// Blacklist all tokens for a user (force logout from all devices)
    /// </summary>
    Task BlacklistUserTokensAsync(string userId, TimeSpan? expiresIn = null);
    
    /// <summary>
    /// Check if a token is blacklisted
    /// </summary>
    Task<bool> IsTokenBlacklistedAsync(string jti);
    
    /// <summary>
    /// Check if all tokens for a user are blacklisted (user-level revocation)
    /// </summary>
    Task<bool> IsUserBlacklistedAsync(string userId, long? tokenIssuedAtUnix = null);
}

