namespace BeeLogistics.Shared.Infrastructure;

/// <summary>
/// Connection settings for one S3-compatible storage profile. Bound as named options
/// (e.g. "S3", "DigitalOcean") so multiple providers can be configured side by side.
/// </summary>
public class ObjectStorageProviderOptions
{
    public string Endpoint { get; set; } = string.Empty;
    /// <summary>Optional CDN hostname (bucket already scoped into the host) used for public URLs instead of the origin endpoint.</summary>
    public string? CdnEndpoint { get; set; }
    /// <summary>Signing region. Must match the provider's expected value (e.g. "sgp1" for DigitalOcean Spaces Singapore); "auto" works for R2.</summary>
    public string Region { get; set; } = "auto";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public bool UseSSL { get; set; } = true;
    public string? BucketName { get; set; }
    public string? ObjectKeyPrefix { get; set; }
}
