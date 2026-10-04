using BeeLogistics.Shared.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BeeLogistics.Tests.Shared;

/// <summary>
/// The Development-only override block re-applies local config over the Vault overlay. It used to
/// keep any value that was `not null`, so a present-but-empty environment variable - which the
/// deploy scripts produce for every unset storage setting via `-e "X=${VAR:-}"` - overwrote the
/// correct Vault value with "". That is how the dev container ended up with a blank
/// DigitalOcean:Endpoint despite Vault holding the right one, and file storage fell to NoOp. #42.
/// </summary>
public class DevelopmentConfigOverridesTests
{
    private static readonly string[] Sections = ["FileStorage", "S3", "DigitalOcean"];

    private static IConfiguration LocalConfig(params (string Key, string? Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_local_value_is_not_collected_so_the_vault_value_survives(string blank)
    {
        var overrides = DevelopmentConfigOverrides.Collect(
            LocalConfig(("DigitalOcean:Endpoint", blank)),
            Sections);

        Assert.Empty(overrides);
    }

    [Fact]
    public void A_real_local_value_still_overrides()
    {
        var overrides = DevelopmentConfigOverrides.Collect(
            LocalConfig(("DigitalOcean:Endpoint", "localhost:8333")),
            Sections);

        Assert.Equal(
            new KeyValuePair<string, string?>("DigitalOcean:Endpoint", "localhost:8333"),
            Assert.Single(overrides));
    }

    [Fact]
    public void Blank_keys_are_dropped_without_taking_their_populated_siblings_with_them()
    {
        var overrides = DevelopmentConfigOverrides.Collect(
            LocalConfig(
                ("DigitalOcean:Endpoint", ""),          // unset CI variable
                ("DigitalOcean:Region", "sgp1"),
                ("DigitalOcean:BucketName", "bee-dev"),
                ("FileStorage:Provider", "DigitalOcean")),
            Sections);

        Assert.DoesNotContain(overrides, kv => kv.Key == "DigitalOcean:Endpoint");
        Assert.Contains(overrides, kv => kv is { Key: "DigitalOcean:Region", Value: "sgp1" });
        Assert.Contains(overrides, kv => kv is { Key: "DigitalOcean:BucketName", Value: "bee-dev" });
        Assert.Contains(overrides, kv => kv is { Key: "FileStorage:Provider", Value: "DigitalOcean" });
    }

    [Fact]
    public void Sections_absent_from_local_config_contribute_nothing()
    {
        var overrides = DevelopmentConfigOverrides.Collect(
            LocalConfig(("Unrelated:Key", "value")),
            Sections);

        Assert.Empty(overrides);
    }

    /// <summary>
    /// End-to-end on the real precedence: env (empty) -> Vault (correct) -> dev overrides last.
    /// This is the deployed dev container's pipeline in miniature.
    /// </summary>
    [Fact]
    public void The_vault_endpoint_survives_the_override_pass_that_previously_blanked_it()
    {
        var local = LocalConfig(
            ("DigitalOcean:Endpoint", ""),
            ("DigitalOcean:Region", "sgp1"));

        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection([new KeyValuePair<string, string?>("DigitalOcean:Endpoint", "")]);
        builder.AddInMemoryCollection([new KeyValuePair<string, string?>("DigitalOcean:Endpoint", "sgp1.digitaloceanspaces.com")]);
        builder.AddInMemoryCollection(DevelopmentConfigOverrides.Collect(local, Sections));

        Assert.Equal("sgp1.digitaloceanspaces.com", builder.Build().GetSection("DigitalOcean")["Endpoint"]);
    }
}
