using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BeeLogistics.Modules.Identity.Application;

namespace BeeLogistics.Api.Middleware;

/// <summary>
/// Middleware to check if JWT tokens are blacklisted
/// Runs after authentication but before authorization
/// </summary>
public class TokenBlacklistMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TokenBlacklistMiddleware> _logger;

    public TokenBlacklistMiddleware(RequestDelegate next, ILogger<TokenBlacklistMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ITokenBlacklistService blacklistService)
    {
        // Skip for unauthenticated requests
        if (!context.User.Identity?.IsAuthenticated ?? true)
        {
            await _next(context);
            return;
        }

        // Get JTI (JWT ID) from token
        var jti = context.User.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                     ?? context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        if (!string.IsNullOrEmpty(jti))
        {
            // Check if this specific token is blacklisted
            if (await blacklistService.IsTokenBlacklistedAsync(jti))
            {
                _logger.LogWarning("Blacklisted token {Jti} attempted access by user {UserId}", jti, userId);
                
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new 
                { 
                    success = false, 
                    message = "Token has been revoked. Please login again." 
                });
                return;
            }
        }

        // Check if all tokens for this user are blacklisted
        if (!string.IsNullOrEmpty(userId))
        {
            var iatClaim = context.User.FindFirst(JwtRegisteredClaimNames.Iat)?.Value;
            long? iat = null;
            if (long.TryParse(iatClaim, out var val)) iat = val;

            if (await blacklistService.IsUserBlacklistedAsync(userId, iat))
            {
                _logger.LogWarning("User {UserId} with blacklisted session attempted access", userId);
                
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new 
                { 
                    success = false, 
                    message = "Your session has been revoked. Please login again." 
                });
                return;
            }
        }

        await _next(context);
    }
}

