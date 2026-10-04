using BeeLogistics.Modules.Messaging.Infrastructure;
using Xunit;

namespace BeeLogistics.Tests.Messaging;

/// <summary>
/// The base-address contract for every call the backend makes to Synapse.
/// </summary>
/// <remarks>
/// This looks trivial and is not. <see cref="Uri"/> relative resolution drops the last path
/// segment when the base has no trailing slash, and drops the whole path when the relative part
/// has a leading one. Either mistake sends requests to a URL that looks right in the logs and
/// 404s at the homeserver.
/// </remarks>
public class MatrixHttpClientTests
{
    [Theory]
    [InlineData("http://beeapp-synapse:8008")]
    [InlineData("http://beeapp-synapse:8008/")]
    [InlineData("https://matrix.mybeeapp.com")]
    [InlineData("https://matrix.mybeeapp.com/")]
    public void Base_address_always_ends_in_a_slash(string configured)
    {
        var normalized = MatrixHttpClient.NormalizeBaseAddress(configured);

        Assert.EndsWith("/", normalized.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Relative_paths_resolve_under_the_configured_host()
    {
        var baseAddress = MatrixHttpClient.NormalizeBaseAddress("http://beeapp-synapse:8008");

        var resolved = new Uri(baseAddress, "_matrix/client/versions");

        Assert.Equal("http://beeapp-synapse:8008/_matrix/client/versions", resolved.ToString());
    }

    [Fact]
    public void A_path_prefix_on_the_homeserver_url_survives_relative_resolution()
    {
        // Synapse behind a proxy that mounts it under a sub-path. This is the case a missing
        // trailing slash silently breaks.
        var baseAddress = MatrixHttpClient.NormalizeBaseAddress("https://mybeeapp.com/matrix");

        var resolved = new Uri(baseAddress, "_matrix/client/versions");

        Assert.Equal("https://mybeeapp.com/matrix/_matrix/client/versions", resolved.ToString());
    }

    [Fact]
    public void A_leading_slash_would_discard_the_path_prefix()
    {
        // Pinned as documentation, not as endorsement: this is exactly why callers must write
        // relative paths without a leading slash. See the remarks on MatrixHttpClient.
        var baseAddress = MatrixHttpClient.NormalizeBaseAddress("https://mybeeapp.com/matrix");

        var resolved = new Uri(baseAddress, "/_matrix/client/versions");

        Assert.Equal("https://mybeeapp.com/_matrix/client/versions", resolved.ToString());
    }
}
