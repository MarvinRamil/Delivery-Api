using BeeLogistics.Shared.Infrastructure;
using Xunit;

namespace BeeLogistics.Tests.Shared;

/// <summary>
/// Public URL construction shares its endpoint normalization with provider registration (#42).
/// These pin the two branches - CDN host (bucket already in the hostname) vs origin host (bucket
/// in the path) - against the real DigitalOcean Spaces values so the refactor cannot change them.
/// </summary>
public class S3CompatiblePublicUrlTests
{
    private static ObjectStorageProviderOptions Options(string endpoint, string? cdnEndpoint, bool useSsl = true) => new()
    {
        Endpoint = endpoint,
        CdnEndpoint = cdnEndpoint,
        AccessKey = "key",
        SecretKey = "secret",
        UseSSL = useSsl,
        BucketName = "bee-dev",
    };

    [Fact]
    public void A_configured_cdn_endpoint_wins_and_omits_the_bucket_segment()
    {
        var url = S3CompatibleFileStorageProvider.BuildPublicUrl(
            Options("sgp1.digitaloceanspaces.com", "https://bee-dev.sgp1.cdn.digitaloceanspaces.com"),
            "bee-dev",
            "application-documents/driver/1.jpg");

        Assert.Equal(
            "https://bee-dev.sgp1.cdn.digitaloceanspaces.com/application-documents/driver/1.jpg",
            url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://")]
    public void A_missing_or_unusable_cdn_endpoint_falls_back_to_the_origin_with_the_bucket_in_the_path(string? cdnEndpoint)
    {
        var url = S3CompatibleFileStorageProvider.BuildPublicUrl(
            Options("sgp1.digitaloceanspaces.com", cdnEndpoint),
            "bee-dev",
            "application-documents/driver/1.jpg");

        Assert.Equal(
            "https://sgp1.digitaloceanspaces.com/bee-dev/application-documents/driver/1.jpg",
            url);
    }

    [Fact]
    public void UseSSL_false_downgrades_the_scheme()
    {
        var url = S3CompatibleFileStorageProvider.BuildPublicUrl(
            Options("localhost:8333", null, useSsl: false),
            "bee-dev",
            "a/b.jpg");

        Assert.Equal("http://localhost:8333/bee-dev/a/b.jpg", url);
    }

    [Fact]
    public void A_blank_endpoint_throws_rather_than_returning_a_schemeless_url()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            S3CompatibleFileStorageProvider.BuildPublicUrl(Options("", null), "bee-dev", "a/b.jpg"));

        Assert.Contains("cannot build a public URL", ex.Message);
    }
}
