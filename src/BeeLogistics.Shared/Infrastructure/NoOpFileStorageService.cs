using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Shared.Infrastructure;

/// <summary>
/// No-op implementation of IFileStorageService for when MinIO is unavailable
/// </summary>
public class NoOpFileStorageService : IFileStorageProvider
{
    public string ProviderName => "NoOp";

    public Task<string> UploadFileAsync(
        Stream stream,
        string bucketName,
        string objectName,
        string contentType,
        CancellationToken ct = default)
    {
        // Return a placeholder URL - file operations will fail but won't crash the app
        return Task.FromResult($"file://{bucketName}/{objectName}");
    }

    public Task DeleteFileAsync(string bucketName, string objectName, CancellationToken ct = default)
    {
        // No-op - do nothing
        return Task.CompletedTask;
    }

    public Task<bool> FileExistsAsync(string bucketName, string objectName, CancellationToken ct = default)
    {
        // Always return false since we can't check
        return Task.FromResult(false);
    }

    public Task<string> GetPresignedUrlAsync(
        string bucketName,
        string objectName,
        int expiresInSeconds = 3600,
        CancellationToken ct = default)
    {
        // Return a placeholder URL
        return Task.FromResult($"file://{bucketName}/{objectName}");
    }

    public Task<string> GetPresignedUrlWithFullKeyAsync(
        string fullBucketName,
        string fullObjectKey,
        int expiresInSeconds = 3600,
        CancellationToken ct = default)
    {
        return Task.FromResult($"file://{fullBucketName}/{fullObjectKey}");
    }
}
