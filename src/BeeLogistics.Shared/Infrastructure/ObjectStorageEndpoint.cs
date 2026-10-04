namespace BeeLogistics.Shared.Infrastructure;

/// <summary>
/// Normalizes an S3-compatible endpoint setting into the bare "host[:port]" form the storage
/// client expects. Split out of Program.cs so the rules are unit-testable: before issue #42 a
/// declared-but-empty endpoint (empty CI/CD variable, blank secret) was handed straight to
/// MinioClient.WithEndpoint(""), which threw and left the provider unregistered.
/// </summary>
public static class ObjectStorageEndpoint
{
    private static readonly string[] Schemes = ["https://", "http://"];

    /// <summary>
    /// Strips the scheme, surrounding whitespace and trailing slashes from <paramref name="raw"/>.
    /// Returns false when nothing usable is left - null, empty, whitespace, or a bare scheme like
    /// "https://" - so callers can treat it as a configuration error instead of forwarding an
    /// empty string to the storage client.
    /// </summary>
    public static bool TryNormalize(string? raw, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var value = raw.Trim();

        foreach (var scheme in Schemes)
        {
            if (value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                value = value[scheme.Length..];
                break;
            }
        }

        value = value.Trim().TrimEnd('/').Trim();

        if (value.Length == 0)
            return false;

        normalized = value;
        return true;
    }
}
