namespace BeeLogistics.Shared.Abstractions;

/// <summary>
/// Interface for file storage operations (S3, local, etc.)
/// </summary>
public interface IFileStorageService
{
    /// <summary>
    /// Upload a file to storage
    /// </summary>
    /// <param name="stream">File stream</param>
    /// <param name="bucketName">Bucket/folder name</param>
    /// <param name="objectName">Object/file name (should be unique, e.g., GUID-based)</param>
    /// <param name="contentType">Content type (e.g., "image/webp")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Public URL to access the file</returns>
    Task<string> UploadFileAsync(
        Stream stream,
        string bucketName,
        string objectName,
        string contentType,
        CancellationToken ct = default);

    /// <summary>
    /// Delete a file from storage
    /// </summary>
    /// <param name="bucketName">Bucket/folder name</param>
    /// <param name="objectName">Object/file name</param>
    /// <param name="ct">Cancellation token</param>
    Task DeleteFileAsync(string bucketName, string objectName, CancellationToken ct = default);

    /// <summary>
    /// Check if a file exists
    /// </summary>
    /// <param name="bucketName">Bucket/folder name</param>
    /// <param name="objectName">Object/file name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if file exists</returns>
    Task<bool> FileExistsAsync(string bucketName, string objectName, CancellationToken ct = default);

    /// <summary>
    /// Get a presigned URL for temporary access (optional, for private files)
    /// </summary>
    /// <param name="bucketName">Bucket/folder name</param>
    /// <param name="objectName">Object/file name</param>
    /// <param name="expiresInSeconds">URL expiration time in seconds</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Presigned URL</returns>
    Task<string> GetPresignedUrlAsync(
        string bucketName,
        string objectName,
        int expiresInSeconds = 3600,
        CancellationToken ct = default);

    /// <summary>
    /// Get a presigned URL using the full bucket name and full object key as stored (no prefix/transform).
    /// Use when resolving a stored URL that already contains the full path (e.g. from UploadFileAsync return or parsed R2 URL).
    /// </summary>
    /// <param name="fullBucketName">Full bucket name as in storage</param>
    /// <param name="fullObjectKey">Full object key as in storage (e.g. test/profiles/userId/guid.webp)</param>
    /// <param name="expiresInSeconds">URL expiration time in seconds</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Presigned or viewable URL</returns>
    Task<string> GetPresignedUrlWithFullKeyAsync(
        string fullBucketName,
        string fullObjectKey,
        int expiresInSeconds = 3600,
        CancellationToken ct = default);
}
