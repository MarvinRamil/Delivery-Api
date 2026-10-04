using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;

namespace BeeLogistics.Modules.Identity.Application.Services;

public interface IRefreshTokenService
{
    Task<RefreshToken> GenerateRefreshTokenAsync(string userId, string? deviceId = null, string? deviceFingerprint = null, CancellationToken ct = default);
    Task<RefreshToken?> ValidateRefreshTokenAsync(string token, CancellationToken ct = default);
    Task RevokeRefreshTokenAsync(string token, string? replacedByToken = null, CancellationToken ct = default);
    Task RevokeAllUserRefreshTokensAsync(string userId, CancellationToken ct = default);
    Task CleanupExpiredTokensAsync(CancellationToken ct = default);
}

public class RefreshTokenService : IRefreshTokenService
{
    private readonly IdentityAppDbContext _context;
    private readonly IConfiguration _configuration;

    public RefreshTokenService(IdentityAppDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    /// <summary>
    /// Generate a new refresh token for a user
    /// SECURITY: Optional device fingerprinting for enhanced security (OWASP Top 10 - A07:2021)
    /// </summary>
    public async Task<RefreshToken> GenerateRefreshTokenAsync(string userId, string? deviceId = null, string? deviceFingerprint = null, CancellationToken ct = default)
    {
        // Generate cryptographically secure random token
        var tokenBytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(tokenBytes);
        var token = Convert.ToBase64String(tokenBytes);

        // Get expiry from config (default 7 days for backoffice)
        var refreshTokenExpiryDays = _configuration.GetValue<int>("JwtSettings:RefreshTokenExpiryDays", 7);
        var expiresAt = DateTime.UtcNow.AddDays(refreshTokenExpiryDays);

        // SECURITY: Device fingerprinting for enhanced security (OWASP Top 10 - A07:2021)
        var refreshToken = new RefreshToken(userId, token, expiresAt, customerId: null, deviceId: deviceId, deviceFingerprint: deviceFingerprint);

        _context.RefreshTokens.Add(refreshToken);
        await _context.SaveChangesAsync(ct);

        return refreshToken;
    }

    /// <summary>
    /// Validate a refresh token and return it if valid
    /// </summary>
    public async Task<RefreshToken?> ValidateRefreshTokenAsync(string token, CancellationToken ct = default)
    {
        var refreshToken = await _context.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.Token == token, ct);

        if (refreshToken == null)
            return null;

        // Check if token is active (not revoked and not expired)
        if (!refreshToken.IsActive)
            return null;

        return refreshToken;
    }

    /// <summary>
    /// Revoke a refresh token (optionally with replacement token for rotation)
    /// </summary>
    public async Task RevokeRefreshTokenAsync(string token, string? replacedByToken = null, CancellationToken ct = default)
    {
        var refreshToken = await _context.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.Token == token, ct);

        if (refreshToken != null)
        {
            refreshToken.Revoke(replacedByToken);
            await _context.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Revoke all refresh tokens for a user (used on password change, logout-all, etc.)
    /// </summary>
    public async Task RevokeAllUserRefreshTokensAsync(string userId, CancellationToken ct = default)
    {
        var tokens = await _context.RefreshTokens
            .Where(rt => rt.UserId == userId && !rt.IsRevoked)
            .ToListAsync(ct);

        foreach (var token in tokens)
        {
            token.Revoke();
        }

        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Clean up expired tokens (can be run as a background job)
    /// </summary>
    public async Task CleanupExpiredTokensAsync(CancellationToken ct = default)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-30); // Keep revoked tokens for 30 days for audit
        
        var expiredTokens = await _context.RefreshTokens
            .Where(rt => rt.ExpiresAt < cutoffDate || (rt.IsRevoked && rt.RevokedAt < cutoffDate))
            .ToListAsync(ct);

        _context.RefreshTokens.RemoveRange(expiredTokens);
        await _context.SaveChangesAsync(ct);
    }
}
