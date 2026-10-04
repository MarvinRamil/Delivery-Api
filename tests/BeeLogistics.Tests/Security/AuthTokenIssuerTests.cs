using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BeeLogistics.Modules.Identity.Application.Services;
using BeeLogistics.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Security;

/// <summary>
/// Locks in the claim matrix that <see cref="AuthTokenIssuer"/> inherited from the four
/// hand-rolled token-minting blocks that used to live in AuthController.
///
/// These assertions describe what the system does TODAY, drift included. Two of them encode
/// behaviour that is probably wrong (backoffice tokens missing full_name; refresh granting
/// is_backoffice by role). They are deliberately pinned so the remaining AuthController
/// refactor cannot change auth behaviour by accident - changing them must be a decision,
/// and that decision is issue #45.
/// </summary>
public class AuthTokenIssuerTests
{
    private const string Secret = "test-secret-that-is-long-enough-for-hmac-sha256-signing";

    private static AuthTokenIssuer Issuer(ApplicationUser user, params string[] roles)
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var userManager = Substitute.For<UserManager<ApplicationUser>>(
            store, null!, null!, null!, null!, null!, null!, null!, null!);
        userManager.GetRolesAsync(user).Returns(roles.ToList() as IList<string>);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JwtSettings:Secret"] = Secret,
            ["JwtSettings:Issuer"] = "BeeLogisticsApi",
            ["JwtSettings:Audience"] = "BeeLogisticsClient",
            ["JwtSettings:ExpiryMinutes"] = "60"
        }).Build();

        return new AuthTokenIssuer(userManager, config);
    }

    private static ApplicationUser User(string role = "Driver") => new()
    {
        Id = "11111111-1111-1111-1111-111111111111",
        Email = "driver@example.com",
        FullName = "Test Driver",
        Role = role
    };

    private static async Task<JwtSecurityToken> Issue(ApplicationUser user, AuthTokenProfile profile, params string[] roles)
    {
        var issued = await Issuer(user, roles).IssueAsync(user, profile);
        return new JwtSecurityTokenHandler().ReadJwtToken(issued.Token);
    }

    private static string? Claim(JwtSecurityToken token, string type)
        => token.Claims.FirstOrDefault(c => c.Type == type)?.Value;

    [Theory]
    [InlineData(AuthTokenProfile.BackofficeLogin)]
    [InlineData(AuthTokenProfile.Frontend)]
    [InlineData(AuthTokenProfile.Refresh)]
    public async Task Every_profile_carries_the_common_identity_claims(AuthTokenProfile profile)
    {
        var user = User();

        var token = await Issue(user, profile);

        Assert.Equal(user.Id, Claim(token, JwtRegisteredClaimNames.Sub));
        Assert.Equal(user.Email, Claim(token, ClaimTypes.Email));
        Assert.Equal(user.Role, Claim(token, "role"));
        Assert.False(string.IsNullOrWhiteSpace(Claim(token, JwtRegisteredClaimNames.Jti)));
    }

    [Fact]
    public async Task Jti_is_unique_per_token_so_blacklisting_targets_one_token()
    {
        var user = User();

        var first = await Issue(user, AuthTokenProfile.Frontend);
        var second = await Issue(user, AuthTokenProfile.Frontend);

        Assert.NotEqual(Claim(first, JwtRegisteredClaimNames.Jti), Claim(second, JwtRegisteredClaimNames.Jti));
    }

    [Fact]
    public async Task Role_claims_from_the_user_manager_are_included()
    {
        var user = User();

        var token = await Issue(user, AuthTokenProfile.Frontend, "Driver", "Customer");

        var roleClaims = token.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList();
        Assert.Contains("Driver", roleClaims);
        Assert.Contains("Customer", roleClaims);
    }

    // --- the preserved drift ------------------------------------------------

    [Fact]
    public async Task Backoffice_login_sets_is_backoffice_and_omits_full_name()
    {
        var token = await Issue(User("Admin"), AuthTokenProfile.BackofficeLogin);

        Assert.Equal("true", Claim(token, "is_backoffice"));
        // Drift, pinned deliberately: every other profile carries full_name.
        Assert.Null(Claim(token, "full_name"));
    }

    [Fact]
    public async Task Frontend_sets_full_name_and_never_sets_is_backoffice()
    {
        var token = await Issue(User("Driver"), AuthTokenProfile.Frontend);

        Assert.Equal("Test Driver", Claim(token, "full_name"));
        Assert.Null(Claim(token, "is_backoffice"));
    }

    [Fact]
    public async Task Frontend_withholds_is_backoffice_even_for_an_admin()
    {
        // This is the half of the drift that behaves correctly: signing in through the regular
        // login endpoint must not confer backoffice access.
        var token = await Issue(User("SuperAdmin"), AuthTokenProfile.Frontend);

        Assert.Null(Claim(token, "is_backoffice"));
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("Admin")]
    public async Task Refresh_grants_is_backoffice_by_role(string role)
    {
        // Pinned, and known to contradict BackofficeAuthorizationMiddleware's comment that
        // is_backoffice is "set only by backoffice-login". An admin who signed in through the
        // regular endpoint gains the claim on their first refresh. See issue #45.
        var token = await Issue(User(role), AuthTokenProfile.Refresh);

        Assert.Equal("true", Claim(token, "is_backoffice"));
        Assert.Equal("Test Driver", Claim(token, "full_name"));
    }

    [Theory]
    [InlineData("Driver")]
    [InlineData("Customer")]
    public async Task Refresh_withholds_is_backoffice_for_non_admin_roles(string role)
    {
        var token = await Issue(User(role), AuthTokenProfile.Refresh);

        Assert.Null(Claim(token, "is_backoffice"));
    }

    // --- signing / expiry ---------------------------------------------------

    [Fact]
    public async Task Token_is_signed_with_the_configured_issuer_audience_and_expiry()
    {
        var user = User();

        var issued = await Issuer(user).IssueAsync(user, AuthTokenProfile.Frontend);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(issued.Token);

        Assert.Equal("BeeLogisticsApi", token.Issuer);
        Assert.Contains("BeeLogisticsClient", token.Audiences);
        Assert.Equal(token.ValidTo, issued.ExpiresAt);
        // 60 minutes from config, allowing a little slack for test execution time.
        Assert.InRange(issued.ExpiresAt, DateTime.UtcNow.AddMinutes(58), DateTime.UtcNow.AddMinutes(62));
    }
}
