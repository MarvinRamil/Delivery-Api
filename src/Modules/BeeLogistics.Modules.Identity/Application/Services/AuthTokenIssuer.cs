using BeeLogistics.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace BeeLogistics.Modules.Identity.Application.Services;

/// <summary>
/// Which flavour of access token to mint.
/// <para>
/// These exist because the four call sites in <c>AuthController</c> historically built their
/// claim lists by hand and drifted apart. The differences are preserved here verbatim rather
/// than silently normalised - see the table on <see cref="AuthTokenIssuer"/>. Collapsing them
/// is a behavioural decision tracked in issue #45, not something this extraction decides.
/// </para>
/// </summary>
public enum AuthTokenProfile
{
    /// <summary>Backoffice sign-in: always carries <c>is_backoffice</c>, carries no <c>full_name</c>.</summary>
    BackofficeLogin,

    /// <summary>App/web sign-in and post-registration auto-login: carries <c>full_name</c>, never <c>is_backoffice</c>.</summary>
    Frontend,

    /// <summary>Token refresh: carries <c>full_name</c>, and <c>is_backoffice</c> when the user's role is SuperAdmin/Admin.</summary>
    Refresh
}

/// <summary>An issued access token and the moment it expires.</summary>
public sealed record IssuedAccessToken(string Token, DateTime ExpiresAt);

public interface IAuthTokenIssuer
{
    /// <summary>
    /// Mint a signed access token for <paramref name="user"/>, with the claim set described by
    /// <paramref name="profile"/>. Role claims are read through <see cref="UserManager{TUser}"/>.
    /// </summary>
    Task<IssuedAccessToken> IssueAsync(ApplicationUser user, AuthTokenProfile profile, CancellationToken ct = default);
}

/// <summary>
/// The single place an access token is constructed.
/// </summary>
/// <remarks>
/// Claim matrix, preserved exactly as the four original call sites behaved:
///
/// <code>
/// profile           sub jti email role full_name is_backoffice
/// BackofficeLogin    x   x    x    x       -          always
/// Frontend           x   x    x    x       x            -
/// Refresh            x   x    x    x       x      if SuperAdmin/Admin
/// </code>
///
/// Two of those cells are almost certainly unintended:
/// <list type="bullet">
/// <item><c>BackofficeLogin</c> omitting <c>full_name</c>, which other paths rely on for chat display.</item>
/// <item><c>Refresh</c> granting <c>is_backoffice</c> by role, while
/// <c>BackofficeAuthorizationMiddleware</c> documents that claim as "set only by backoffice-login".
/// A regular-login admin therefore gains backoffice access after one refresh.</item>
/// </list>
/// Both are left as-is here so this extraction changes no behaviour. Fixing them is issue #45.
/// </remarks>
public class AuthTokenIssuer : IAuthTokenIssuer
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;

    public AuthTokenIssuer(UserManager<ApplicationUser> userManager, IConfiguration configuration)
    {
        _userManager = userManager;
        _configuration = configuration;
    }

    public async Task<IssuedAccessToken> IssueAsync(ApplicationUser user, AuthTokenProfile profile, CancellationToken ct = default)
    {
        var roles = await _userManager.GetRolesAsync(user);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),           // User ID - required for all operations
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()), // Token ID - required for blacklisting
            new(ClaimTypes.Email, user.Email ?? string.Empty),   // Email - for booking customer resolution
            new("role", user.Role)                               // User role - required for authorization
        };

        // Kept for chat performance (read frequently in real-time). Absent on backoffice tokens.
        if (profile is AuthTokenProfile.Frontend or AuthTokenProfile.Refresh)
        {
            claims.Add(new Claim("full_name", user.FullName));
        }

        if (ShouldIncludeBackofficeClaim(profile, user))
        {
            claims.Add(new Claim("is_backoffice", "true"));
        }

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var jwtSettings = _configuration.GetSection("JwtSettings");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["Secret"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: jwtSettings["Issuer"],
            audience: jwtSettings["Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(double.Parse(jwtSettings["ExpiryMinutes"]!)),
            signingCredentials: creds
        );

        return new IssuedAccessToken(new JwtSecurityTokenHandler().WriteToken(token), token.ValidTo);
    }

    private static bool ShouldIncludeBackofficeClaim(AuthTokenProfile profile, ApplicationUser user) => profile switch
    {
        AuthTokenProfile.BackofficeLogin => true,
        AuthTokenProfile.Refresh => user.Role is "SuperAdmin" or "Admin",
        _ => false
    };
}
