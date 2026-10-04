using BeeLogistics.Shared.Infrastructure;
using Xunit;

namespace BeeLogistics.Tests.Shared;

/// <summary>
/// Endpoint resolution used to be `section["Endpoint"] ?? "localhost:8333"`, which only guarded
/// against null. A declared-but-empty setting (empty CI/CD variable, blank Vault value) therefore
/// reached the storage client as "" and threw ArgumentException, and the caller downgraded that to
/// a warning - so uploads silently fell through to NoOp. Issue #42.
/// </summary>
public class ObjectStorageEndpointTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Missing_or_blank_endpoints_are_rejected(string? raw)
    {
        Assert.False(ObjectStorageEndpoint.TryNormalize(raw, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Theory]
    [InlineData("https://")]
    [InlineData("http://")]
    [InlineData("https:///")]
    [InlineData("  https://  ")]
    public void A_bare_scheme_leaves_no_host_and_is_rejected(string raw)
    {
        Assert.False(ObjectStorageEndpoint.TryNormalize(raw, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Theory]
    [InlineData("sgp1.digitaloceanspaces.com", "sgp1.digitaloceanspaces.com")]
    [InlineData("https://sgp1.digitaloceanspaces.com", "sgp1.digitaloceanspaces.com")]
    [InlineData("http://sgp1.digitaloceanspaces.com/", "sgp1.digitaloceanspaces.com")]
    [InlineData("HTTPS://Sgp1.DigitalOceanSpaces.com//", "Sgp1.DigitalOceanSpaces.com")]
    [InlineData("  https://d20e.r2.cloudflarestorage.com/  ", "d20e.r2.cloudflarestorage.com")]
    [InlineData("localhost:8333", "localhost:8333")]
    [InlineData("http://localhost:8333/", "localhost:8333")]
    public void Scheme_whitespace_and_trailing_slashes_are_stripped(string raw, string expected)
    {
        Assert.True(ObjectStorageEndpoint.TryNormalize(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Host_casing_is_preserved_even_though_the_scheme_match_is_case_insensitive()
    {
        Assert.True(ObjectStorageEndpoint.TryNormalize("HtTpS://SGP1.example.com", out var normalized));
        Assert.Equal("SGP1.example.com", normalized);
    }
}
