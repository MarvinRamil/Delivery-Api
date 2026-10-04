using System.Security.Claims;
using BeeLogistics.Shared.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Identity.Infrastructure;

/// <summary>
/// Roles are owned locally, never by Clerk. For a Clerk-issued token (which carries
/// only identity, no role), this resolves the local <c>ApplicationUser</c> by
/// <c>ClerkUserId</c> and injects the authoritative role claims so existing
/// <c>[Authorize(Roles=...)]</c> attributes and the Backoffice policy work unchanged.
///
/// Legacy HS256 tokens already include role/is_backoffice claims, so they are left
/// untouched. The lookup is cached briefly to avoid a DB hit on every request.
/// </summary>
public class LocalRoleClaimsTransformation : IClaimsTransformation
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    // Roles that may access backoffice endpoints (mirrors the "Backoffice" policy).
    private static readonly HashSet<string> BackofficeRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        UserRoles.SuperAdmin,
        "Admin", // legacy staff role still referenced by some [Authorize] attributes
    };

    private readonly IdentityAppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<LocalRoleClaimsTransformation> _logger;

    public LocalRoleClaimsTransformation(
        IdentityAppDbContext db,
        IMemoryCache cache,
        ILogger<LocalRoleClaimsTransformation> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = principal.Identity as ClaimsIdentity;
        if (identity is null || !identity.IsAuthenticated)
        {
            return principal;
        }

        // Legacy tokens already carry role claims — nothing to do. This also makes
        // the transform idempotent if it runs more than once for a request.
        if (identity.HasClaim(c => c.Type == ClaimTypes.Role || c.Type == "role"))
        {
            return principal;
        }

        var clerkUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");
        if (string.IsNullOrEmpty(clerkUserId))
        {
            return principal;
        }

        var info = await GetUserRoleInfoAsync(clerkUserId);
        if (info is null)
        {
            // No local profile yet (e.g. webhook hasn't synced). Leave unauthorized-for-roles.
            _logger.LogWarning("[ClaimsTransform] No local user for Clerk id {ClerkUserId}", clerkUserId);
            return principal;
        }

        // Bridge identity: downstream controllers resolve the user via
        // FindByIdAsync(NameIdentifier) expecting the LOCAL ApplicationUser.Id, but a
        // Clerk token carries the Clerk id in NameIdentifier/sub. Swap it to the local
        // id so /api/auth/me and all id-based lookups work unchanged. The original
        // Clerk id is preserved under "clerk_id" for reference.
        foreach (var c in identity.FindAll(ClaimTypes.NameIdentifier).ToList())
        {
            identity.RemoveClaim(c);
        }
        identity.AddClaim(new Claim("clerk_id", clerkUserId));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, info.LocalId));

        identity.AddClaim(new Claim("role", info.Role));
        identity.AddClaim(new Claim(ClaimTypes.Role, info.Role));
        if (BackofficeRoles.Contains(info.Role))
        {
            identity.AddClaim(new Claim("is_backoffice", "true"));
        }

        return principal;
    }

    private async Task<UserRoleInfo?> GetUserRoleInfoAsync(string clerkUserId)
    {
        var key = $"clerk-role:{clerkUserId}";
        if (_cache.TryGetValue(key, out UserRoleInfo? cached))
        {
            return cached;
        }

        // Project to avoid decrypting PII columns (FullName/PhoneNumber).
        var info = await _db.Users
            .Where(u => u.ClerkUserId == clerkUserId && u.IsActive)
            .Select(u => new UserRoleInfo(u.Role, u.Id))
            .FirstOrDefaultAsync();

        // Only cache POSITIVE hits. Caching a null (no local user yet — the
        // user.created webhook / just-in-time provisioning on /api/auth/me may not
        // have run) would pin the unresolved state for the whole TTL. During that
        // window NameIdentifier is never swapped to the local ApplicationUser.Id,
        // so [Authorize(Roles=...)] checks fail (403) and liveness verification is
        // recorded against the Clerk id (FindByIdAsync miss → silently lost).
        if (info is not null)
        {
            _cache.Set(key, info, CacheTtl);
        }

        return info;
    }

    private sealed record UserRoleInfo(string Role, string LocalId);
}
