using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Shared.Infrastructure;

/// <summary>
/// Strategy pattern "Context": the only IFileStorageService every consumer injects. Delegates
/// each call to whichever provider IFileStorageProviderFactory currently considers active, so
/// flipping FileStorage:Provider in config changes behavior on the very next call - no restart.
/// </summary>
public class FileStorageRouter : IFileStorageService
{
    private readonly IFileStorageProviderFactory _factory;

    public FileStorageRouter(IFileStorageProviderFactory factory)
    {
        _factory = factory;
    }

    public Task<string> UploadFileAsync(Stream stream, string bucketName, string objectName, string contentType, CancellationToken ct = default)
        => _factory.GetActive().UploadFileAsync(stream, bucketName, objectName, contentType, ct);

    public Task DeleteFileAsync(string bucketName, string objectName, CancellationToken ct = default)
        => _factory.GetActive().DeleteFileAsync(bucketName, objectName, ct);

    public Task<bool> FileExistsAsync(string bucketName, string objectName, CancellationToken ct = default)
        => _factory.GetActive().FileExistsAsync(bucketName, objectName, ct);

    public Task<string> GetPresignedUrlAsync(string bucketName, string objectName, int expiresInSeconds = 3600, CancellationToken ct = default)
        => _factory.GetActive().GetPresignedUrlAsync(bucketName, objectName, expiresInSeconds, ct);

    public Task<string> GetPresignedUrlWithFullKeyAsync(string fullBucketName, string fullObjectKey, int expiresInSeconds = 3600, CancellationToken ct = default)
        => _factory.GetActive().GetPresignedUrlWithFullKeyAsync(fullBucketName, fullObjectKey, expiresInSeconds, ct);
}
