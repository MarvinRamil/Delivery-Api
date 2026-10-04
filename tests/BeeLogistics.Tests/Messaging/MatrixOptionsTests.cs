using BeeLogistics.Modules.Messaging.Application;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BeeLogistics.Tests.Messaging;

/// <summary>
/// <see cref="MatrixOptions.Validate"/> is the single source of truth for "is this appservice
/// usable", shared by the module and by the production fail-fast guard in Program.cs. These tests
/// exist because a half-configured appservice fails in the worst possible way: startup succeeds,
/// bookings are created normally, and the only symptom is that no chat room ever appears — hours
/// later, in production, with no error anywhere.
/// </summary>
public class MatrixOptionsTests
{
    private static MatrixOptions Valid() => new()
    {
        Enabled = true,
        HomeserverUrl = "http://beeapp-synapse:8008",
        ServerName = "matrix.bee-app.tech",
        EnvironmentPrefix = "prod",
        AppServiceId = "bee-appservice-prod",
        SenderLocalpart = "bee",
        AsToken = "as-token-value",
        HsToken = "hs-token-value",
    };

    [Fact]
    public void Disabled_requires_no_configuration_at_all()
    {
        // What lets CI and local dev run with no Matrix configuration whatsoever.
        var options = new MatrixOptions { Enabled = false };

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Fully_configured_is_valid()
    {
        Assert.Empty(Valid().Validate());
    }

    [Theory]
    [InlineData(nameof(MatrixOptions.ServerName))]
    [InlineData(nameof(MatrixOptions.AsToken))]
    [InlineData(nameof(MatrixOptions.HsToken))]
    [InlineData(nameof(MatrixOptions.SenderLocalpart))]
    [InlineData(nameof(MatrixOptions.AppServiceId))]
    [InlineData(nameof(MatrixOptions.EnvironmentPrefix))]
    [InlineData(nameof(MatrixOptions.HomeserverUrl))]
    public void Enabled_rejects_each_missing_required_value(string property)
    {
        var options = Valid();
        typeof(MatrixOptions).GetProperty(property)!.SetValue(options, string.Empty);

        var problems = options.Validate();

        Assert.Contains(problems, p => p.Contains(property, StringComparison.Ordinal));
    }

    [Fact]
    public void Reuse_of_one_secret_for_both_tokens_is_rejected()
    {
        // The two tokens authenticate opposite directions. Sharing a value means anyone who can
        // reach the appservice endpoint also holds the credential that acts as every user.
        var options = Valid();
        options.HsToken = options.AsToken;

        Assert.Contains(options.Validate(), p => p.Contains("must be different secrets", StringComparison.Ordinal));
    }

    [Fact]
    public void ServerName_given_as_a_url_is_rejected()
    {
        // The classic misconfiguration: server_name is the MXID domain, not the homeserver URL.
        // Catching it at startup matters because it is unfixable later — the value is baked into
        // every user and room id ever created.
        var options = Valid();
        options.ServerName = "https://matrix.bee-app.tech";

        Assert.Contains(options.Validate(), p => p.Contains("not a URL", StringComparison.Ordinal));
    }

    [Theory]
    // A host:port typo. Uri.TryCreate calls this "absolute" — scheme beeapp-synapse, path 8008 —
    // so an absoluteness check alone lets it through and HttpClient throws at first use instead.
    [InlineData("beeapp-synapse:8008")]
    [InlineData("//beeapp-synapse:8008")]
    [InlineData("/_matrix")]
    // Right shape, wrong protocol.
    [InlineData("ftp://beeapp-synapse:8008")]
    [InlineData("ws://beeapp-synapse:8008")]
    public void Homeserver_url_must_be_an_absolute_http_url(string url)
    {
        var options = Valid();
        options.HomeserverUrl = url;

        Assert.Contains(options.Validate(), p => p.Contains("absolute http(s) URL", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://beeapp-synapse:8008")]
    [InlineData("https://matrix.mybeeapp.com")]
    [InlineData("https://mybeeapp.com/matrix")]
    public void Usable_homeserver_urls_are_accepted(string url)
    {
        var options = Valid();
        options.HomeserverUrl = url;

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Retention_shorter_than_the_freeze_delay_is_rejected()
    {
        // Would purge rooms that are still open for conversation.
        var options = Valid();
        options.RoomFreezeDelayHours = 72;
        options.RoomRetentionDays = 1;

        Assert.Contains(options.Validate(), p => p.Contains("RoomRetentionDays", StringComparison.Ordinal));
    }

    [Fact]
    public void BotUserId_is_composed_from_localpart_and_server_name()
    {
        Assert.Equal("@bee:matrix.bee-app.tech", Valid().BotUserId);
    }

    [Fact]
    public void Client_facing_url_falls_back_to_the_internal_one_when_unset()
    {
        var options = Valid();

        Assert.Equal(options.HomeserverUrl, options.ClientFacingHomeserverUrl);

        options.PublicHomeserverUrl = "https://matrix.bee-app.tech";
        Assert.Equal("https://matrix.bee-app.tech", options.ClientFacingHomeserverUrl);
    }

    [Fact]
    public void Binds_from_configuration_using_the_section_name()
    {
        // Guards the wiring itself: a renamed section or a mistyped key binds silently to defaults.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Matrix:Enabled"] = "true",
            ["Matrix:ServerName"] = "matrix.bee-app.tech",
            ["Matrix:EnvironmentPrefix"] = "prod",
            ["Matrix:AsToken"] = "as",
            ["Matrix:HsToken"] = "hs",
            ["Matrix:RoomRetentionDays"] = "30",
        }).Build();

        var options = new MatrixOptions();
        config.GetSection(MatrixOptions.SectionName).Bind(options);

        Assert.True(options.Enabled);
        Assert.Equal("matrix.bee-app.tech", options.ServerName);
        Assert.Equal("prod", options.EnvironmentPrefix);
        Assert.Equal(30, options.RoomRetentionDays);
        // Untouched keys keep their defaults rather than becoming null/0.
        Assert.Equal(24, options.RoomFreezeDelayHours);
        Assert.Equal(20, options.TimeoutSeconds);
    }
}
