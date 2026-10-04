using BeeLogistics.Modules.Messaging.Application;
using Xunit;

namespace BeeLogistics.Tests.Messaging;

/// <summary>
/// One Synapse serves every environment, so the naming here is the isolation boundary.
/// </summary>
/// <remarks>
/// <para>
/// The failure this prevents is not cosmetic. <c>BookingNumber</c> is unique only <em>within</em> a
/// database (unique index, <c>BookingsDbContext.cs:92</c>) and it is minted from the clock —
/// <c>BKG-{yyyyMMdd}-{Ticks % 1000000}</c>, <c>Booking.cs:276</c> — so dev and production, which
/// have separate databases, can produce the same booking number on the same day.
/// </para>
/// <para>
/// Room creation treats the alias as an idempotency key and <b>adopts</b> an existing room on
/// <c>M_ROOM_IN_USE</c> rather than failing. Without the environment prefix, a dev booking that
/// happened to draw production's number would silently join a real customer's chat. These tests
/// pin the prefix so that cannot regress into a data leak.
/// </para>
/// <para>
/// The prefix is not the only defence — each environment's appservice declares its namespaces
/// <c>exclusive</c>, so Synapse refuses cross-environment claims server-side. But the prefix is
/// what makes that registration expressible, so it is the piece worth testing here.
/// </para>
/// </remarks>
public class MatrixEnvironmentIsolationTests
{
    private static MatrixOptions For(string prefix) => new()
    {
        Enabled = true,
        HomeserverUrl = "http://beeapp-synapse:8008",
        ServerName = "matrix.bee-app.tech",
        EnvironmentPrefix = prefix,
        AppServiceId = $"bee-appservice-{prefix}",
        SenderLocalpart = prefix == "prod" ? "bee" : $"bee-{prefix}",
        AsToken = "as",
        HsToken = "hs",
    };

    [Fact]
    public void The_same_booking_number_in_two_environments_gets_two_different_rooms()
    {
        // The whole reason EnvironmentPrefix exists.
        const string collidingNumber = "BKG-20260823-000123";

        var dev = For("dev").RoomAliasFor(collidingNumber);
        var prod = For("prod").RoomAliasFor(collidingNumber);

        Assert.NotEqual(dev, prod);
        Assert.Equal("#booking-dev-bkg-20260823-000123:matrix.bee-app.tech", dev);
        Assert.Equal("#booking-prod-bkg-20260823-000123:matrix.bee-app.tech", prod);
    }

    [Fact]
    public void The_same_person_gets_a_different_matrix_account_per_environment()
    {
        var identityUserId = "3f1b9c2e-4a5d-4f6b-8c7d-9e0a1b2c3d4e";

        var dev = For("dev").MatrixUserIdFor(identityUserId);
        var prod = For("prod").MatrixUserIdFor(identityUserId);

        Assert.NotEqual(dev, prod);
        Assert.StartsWith("@bee_u_dev_", dev, StringComparison.Ordinal);
        Assert.StartsWith("@bee_u_prod_", prod, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_environment_gets_its_own_bot_account()
    {
        // Synapse requires a distinct sender_localpart per registration on the same homeserver.
        Assert.NotEqual(For("dev").BotUserId, For("prod").BotUserId);
        Assert.Equal("@bee:matrix.bee-app.tech", For("prod").BotUserId);
        Assert.Equal("@bee-dev:matrix.bee-app.tech", For("dev").BotUserId);
    }

    [Fact]
    public void Bot_accounts_sit_outside_the_user_namespace_they_administer()
    {
        // A sender_localpart inside its own exclusive user namespace is rejected by Synapse at
        // startup, because the appservice would be claiming the account it is registering as.
        foreach (var options in new[] { For("dev"), For("prod") })
        {
            Assert.DoesNotContain(options.UserLocalpartPrefix, options.BotUserId, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Room_aliases_are_lowercased_because_matrix_aliases_are_case_sensitive()
    {
        // BookingNumber is uppercase. If the alias kept that case, a later lookup that normalised
        // it would miss the existing room and create a duplicate for the same booking.
        var options = For("prod");

        var alias = options.RoomAliasFor("BKG-20260823-000123");

        Assert.Equal(alias, alias.ToLowerInvariant());
    }

    [Fact]
    public void User_ids_are_lowercased_so_one_identity_cannot_become_two_accounts()
    {
        // Guids reach us in whatever case the caller used; Matrix localparts are case-sensitive.
        var options = For("prod");

        Assert.Equal(
            options.MatrixUserIdFor("3F1B9C2E-4A5D-4F6B-8C7D-9E0A1B2C3D4E"),
            options.MatrixUserIdFor("3f1b9c2e-4a5d-4f6b-8c7d-9e0a1b2c3d4e"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Prod")]          // uppercase would not match the registration's regex
    [InlineData("prod-1")]        // hyphen collides with the alias separator
    [InlineData("prod_1")]        // underscore collides with the user-localpart separator
    [InlineData("p")]             // too short to be meaningful
    [InlineData("production-environment-name")]
    public void An_unusable_environment_prefix_is_rejected_at_startup(string prefix)
    {
        var options = For("prod");
        options.EnvironmentPrefix = prefix;

        Assert.Contains(options.Validate(), p => p.Contains("EnvironmentPrefix", StringComparison.Ordinal));
    }

    [Fact]
    public void Enabling_a_new_environment_without_a_prefix_says_what_is_missing()
    {
        // The staging path: someone flips MATRIX_ENABLED=true and deploys. Startup must refuse and
        // point at the registration step, rather than booting and colliding with prod's aliases.
        var options = For("prod");
        options.EnvironmentPrefix = "";

        var problem = Assert.Single(options.Validate(), p => p.Contains("EnvironmentPrefix", StringComparison.Ordinal));

        Assert.Contains("exclusive appservice registration", problem, StringComparison.Ordinal);
    }
}
