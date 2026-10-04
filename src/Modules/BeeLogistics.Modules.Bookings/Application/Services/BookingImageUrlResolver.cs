using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// What a stored image path turned out to be. The three cases are distinct on purpose:
/// a caller that serves files from disk has to tell a local path apart from an
/// <see cref="Malformed"/> s3 reference, and must not treat the latter as a filename.
/// </summary>
public enum S3ReferenceKind
{
    /// <summary>A local path — not an s3 reference at all.</summary>
    NotS3,

    /// <summary>Starts with "s3:" but has no bucket/key. A broken record, not a path.</summary>
    Malformed,

    /// <summary>A well-formed s3:bucket:objectKey reference.</summary>
    Parsed
}

/// <summary>
/// Resolves s3:... stored paths to viewable presigned URLs for booking/delivery images.
/// No DB or storage format changes; only affects what is returned in API responses.
/// </summary>
public static class BookingImageUrlResolver
{
    private const int DefaultExpirySeconds = 3600;

    /// <summary>
    /// Parses a stored "s3:bucket:objectKey" reference.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ResolveStoragePathAsync"/>, which returns the original path when it
    /// cannot resolve one, this reports failure. Endpoints that redirect need that: handing
    /// back the unresolved path would redirect the client to the literal "s3:bucket:key".
    /// </remarks>
    public static S3ReferenceKind TryParseS3Reference(string? path, out string bucket, out string objectKey)
    {
        bucket = string.Empty;
        objectKey = string.Empty;

        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("s3:", StringComparison.OrdinalIgnoreCase))
            return S3ReferenceKind.NotS3;

        var parts = path.Split(':', 3, StringSplitOptions.None);
        if (parts.Length < 3)
            return S3ReferenceKind.Malformed;

        bucket = parts[1];
        objectKey = parts[2];
        return S3ReferenceKind.Parsed;
    }

    /// <summary>
    /// If path starts with s3:bucket:key, returns a presigned URL; otherwise returns path unchanged.
    /// </summary>
    public static async Task<string?> ResolveStoragePathAsync(
        string? path,
        IFileStorageService storage,
        int expiresInSeconds = DefaultExpirySeconds,
        CancellationToken ct = default)
    {
        if (storage == null || string.IsNullOrWhiteSpace(path) || !path.StartsWith("s3:", StringComparison.OrdinalIgnoreCase))
            return path;
        var parts = path.Split(':', 3, StringSplitOptions.None);
        if (parts.Length < 3)
            return path;
        try
        {
            return await storage.GetPresignedUrlAsync(parts[1], parts[2], expiresInSeconds, ct);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>
    /// Resolves driver profile picture URL the same way as auth/me: supports s3:bucket:key and full https:// (R2) URLs.
    /// Full URLs are re-presigned via GetPresignedUrlWithFullKeyAsync so they work when the bucket is private.
    /// </summary>
    public static async Task<string?> ResolveProfilePictureUrlAsync(
        string? storedUrl,
        IFileStorageService storage,
        int expiresInSeconds = DefaultExpirySeconds,
        CancellationToken ct = default)
    {
        if (storage == null || string.IsNullOrWhiteSpace(storedUrl))
            return storedUrl;
        try
        {
            if (storedUrl.StartsWith("s3:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = storedUrl.Split(':', 3, StringSplitOptions.None);
                if (parts.Length >= 3)
                    return await storage.GetPresignedUrlAsync(parts[1], parts[2], expiresInSeconds, ct);
                return storedUrl;
            }
            if (storedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || storedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(storedUrl);
                var pathSegments = uri.AbsolutePath.TrimStart('/').Split('/', 2);
                if (pathSegments.Length >= 2)
                    return await storage.GetPresignedUrlWithFullKeyAsync(pathSegments[0], pathSegments[1], expiresInSeconds, ct);
            }
            return storedUrl;
        }
        catch
        {
            return storedUrl;
        }
    }

    public static async Task<BookingDto> ResolveAsync(
        BookingDto dto,
        IFileStorageService storage,
        CancellationToken ct = default)
    {
        if (dto == null || storage == null) return dto;
        var resolvedItem = await ResolveStoragePathAsync(dto.ItemImagePath, storage, DefaultExpirySeconds, ct);
        var resolvedDriverImage = await ResolveProfilePictureUrlAsync(dto.DriverImageUrl, storage, DefaultExpirySeconds, ct);
        var resolvedPods = dto.ProofOfDeliveries?.Count > 0
            ? (await Task.WhenAll(dto.ProofOfDeliveries.Select(p => ResolvePodAsync(p, storage, ct)))).ToList()
            : dto.ProofOfDeliveries;
        var itemChanged = resolvedItem != dto.ItemImagePath;
        var driverChanged = resolvedDriverImage != dto.DriverImageUrl;
        var podsChanged = resolvedPods != dto.ProofOfDeliveries;
        if (!itemChanged && !driverChanged && !podsChanged)
            return dto;
        return dto with
        {
            ItemImagePath = resolvedItem ?? dto.ItemImagePath,
            DriverImageUrl = resolvedDriverImage ?? dto.DriverImageUrl,
            ProofOfDeliveries = resolvedPods ?? dto.ProofOfDeliveries
        };
    }

    public static async Task<IReadOnlyList<BookingDto>> ResolveListAsync(
        IReadOnlyList<BookingDto> dtos,
        IFileStorageService storage,
        CancellationToken ct = default)
    {
        if (dtos == null || dtos.Count == 0 || storage == null)
            return dtos ?? Array.Empty<BookingDto>();
        var list = new List<BookingDto>(dtos.Count);
        foreach (var dto in dtos)
            list.Add(await ResolveAsync(dto, storage, ct));
        return list;
    }

    public static async Task<DriverBookingOfferWithDetailsDto> ResolveOfferAsync(
        DriverBookingOfferWithDetailsDto dto,
        IFileStorageService storage,
        CancellationToken ct = default)
    {
        if (dto == null || storage == null) return dto;
        var resolved = await ResolveStoragePathAsync(dto.ItemImagePath, storage, DefaultExpirySeconds, ct);
        if (resolved == dto.ItemImagePath)
            return dto;
        return dto with { ItemImagePath = resolved };
    }

    public static async Task<ProofOfDeliveryDto> ResolvePodAsync(
        ProofOfDeliveryDto dto,
        IFileStorageService storage,
        CancellationToken ct = default)
    {
        if (dto == null || storage == null) return dto;
        var imagePath = await ResolveStoragePathAsync(dto.ImagePath, storage, DefaultExpirySeconds, ct);
        var signaturePath = await ResolveStoragePathAsync(dto.SignaturePath, storage, DefaultExpirySeconds, ct);
        if (imagePath == dto.ImagePath && signaturePath == dto.SignaturePath)
            return dto;
        return dto with { ImagePath = imagePath ?? dto.ImagePath, SignaturePath = signaturePath };
    }
}
