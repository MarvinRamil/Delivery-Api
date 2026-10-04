using BeeLogistics.Shared.Abstractions;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Identity.Application.Services;

public interface IProfilePictureUrlResolver
{
    /// <summary>
    /// Resolve a stored profile-picture reference to a URL a client can actually fetch.
    /// Returns the input unchanged when there is nothing to resolve or resolution fails.
    /// </summary>
    Task<string?> ResolveAsync(string? storedUrl, CancellationToken ct = default);
}

/// <summary>
/// Resolves a stored R2/S3 reference to a presigned URL that clients can access.
/// <para>
/// R2 buckets are private by default, so direct URLs won't work for mobile apps. Presigned URLs
/// grant temporary read access (7 days). Generating them is a local crypto operation (no network
/// call to R2), so it is essentially free - a fresh URL is generated on every login / token
/// refresh / profile fetch.
/// </para>
/// </summary>
public class ProfilePictureUrlResolver : IProfilePictureUrlResolver
{
    private const int PresignedUrlExpirySeconds = 7 * 24 * 3600; // 7 days

    private readonly IFileStorageService? _fileStorageService;
    private readonly ILogger<ProfilePictureUrlResolver>? _logger;

    public ProfilePictureUrlResolver(
        IFileStorageService? fileStorageService = null,
        ILogger<ProfilePictureUrlResolver>? logger = null)
    {
        _fileStorageService = fileStorageService;
        _logger = logger;
    }

    public async Task<string?> ResolveAsync(string? storedUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(storedUrl) || _fileStorageService == null)
            return storedUrl;

        try
        {
            // Stored format can be: (1) s3:profiles:objectName (logical bucket + key), or (2) full R2 URL https://{endpoint}/{bucket}/{key}
            if (storedUrl.StartsWith("s3:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = storedUrl.Split(':', 3, StringSplitOptions.None);
                if (parts.Length >= 3)
                    return await _fileStorageService.GetPresignedUrlAsync(parts[1], parts[2], PresignedUrlExpirySeconds, ct);
                return storedUrl;
            }

            if (storedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || storedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // Full URL from UploadFileAsync: path is {bucket}/{fullObjectKey} - use full-key presign to avoid double prefix
                var uri = new Uri(storedUrl);
                var pathSegments = uri.AbsolutePath.TrimStart('/').Split('/', 2);
                if (pathSegments.Length >= 2)
                    return await _fileStorageService.GetPresignedUrlWithFullKeyAsync(pathSegments[0], pathSegments[1], PresignedUrlExpirySeconds, ct);
            }

            return storedUrl;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to generate presigned URL for profile picture, falling back to stored URL");
            return storedUrl;
        }
    }
}
