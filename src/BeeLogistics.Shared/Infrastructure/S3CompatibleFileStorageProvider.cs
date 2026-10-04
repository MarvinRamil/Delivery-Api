using BeeLogistics.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel;

namespace BeeLogistics.Shared.Infrastructure;

/// <summary>
/// S3-compatible file storage strategy using MinIO. Works against any S3-compatible provider
/// (Cloudflare R2, DigitalOcean Spaces, AWS S3, MinIO server, ...) - the same class is
/// instantiated once per configured provider profile (see FileStorageProviderFactory).
/// </summary>
public class S3CompatibleFileStorageProvider : IFileStorageProvider
{
    private readonly IMinioClient _minioClient;
    private readonly ILogger<S3CompatibleFileStorageProvider> _logger;
    private readonly ObjectStorageProviderOptions _options;
    private readonly string? _bucketName;
    private readonly string _objectKeyPrefix;

    public string ProviderName { get; }

    public S3CompatibleFileStorageProvider(
        IMinioClient minioClient,
        ObjectStorageProviderOptions options,
        string providerName,
        ILogger<S3CompatibleFileStorageProvider> logger)
    {
        _minioClient = minioClient;
        _options = options;
        ProviderName = providerName;
        _logger = logger;
        _bucketName = options.BucketName?.Trim();
        _objectKeyPrefix = (options.ObjectKeyPrefix ?? "").Trim().Replace('\\', '/');
        if (!string.IsNullOrEmpty(_objectKeyPrefix) && !_objectKeyPrefix.EndsWith('/'))
            _objectKeyPrefix += "/";
    }

    /// <summary>
    /// Resolves bucket name: use configured BucketName when set (single bucket), otherwise use logical bucket from caller.
    /// </summary>
    private string GetFullBucketName(string logicalBucketName)
    {
        return !string.IsNullOrEmpty(_bucketName) ? _bucketName : logicalBucketName;
    }

    /// <summary>
    /// Resolves object key: when using single bucket, prepend ObjectKeyPrefix and logical bucket as folder.
    /// e.g. ObjectKeyPrefix=test/, logicalBucket=profiles, objectName=userId/guid.webp → test/profiles/userId/guid.webp
    /// </summary>
    private string GetObjectKey(string logicalBucketName, string objectName)
    {
        var key = objectName.Replace('\\', '/').TrimStart('/');
        if (!string.IsNullOrEmpty(_bucketName))
            key = _objectKeyPrefix + logicalBucketName.Trim('/') + "/" + key;
        else if (!string.IsNullOrEmpty(_objectKeyPrefix))
            key = _objectKeyPrefix + key;
        return key;
    }

    /// <summary>
    /// Ensure bucket exists, create if it doesn't.
    /// Some providers (e.g. Cloudflare R2) require buckets to be created manually in the dashboard
    /// or an API token with "Admin Read & Write" permissions (not just "Object Read & Write").
    /// </summary>
    private async Task EnsureBucketExistsAsync(string bucketName, CancellationToken ct)
    {
        try
        {
            var bucketExistsArgs = new BucketExistsArgs()
                .WithBucket(bucketName);

            var exists = await _minioClient.BucketExistsAsync(bucketExistsArgs, ct);

            if (!exists)
            {
                _logger.LogInformation("[{Provider}] Bucket {BucketName} does not exist, attempting to create it...", ProviderName, bucketName);
                try
                {
                    var makeBucketArgs = new MakeBucketArgs()
                        .WithBucket(bucketName);

                    await _minioClient.MakeBucketAsync(makeBucketArgs, ct);
                    _logger.LogInformation("[{Provider}] Successfully created bucket: {BucketName}", ProviderName, bucketName);

                    // Verify bucket was created by checking again
                    exists = await _minioClient.BucketExistsAsync(bucketExistsArgs, ct);
                    if (!exists)
                    {
                        throw new InvalidOperationException(
                            $"Bucket {bucketName} creation appeared to succeed but bucket still does not exist. " +
                            "Ensure the bucket exists in the provider's dashboard or the API credentials have admin read/write permissions.");
                    }
                }
                catch (Exception createEx)
                {
                    // If bucket creation fails, check if it exists now (might have been created concurrently)
                    try
                    {
                        exists = await _minioClient.BucketExistsAsync(bucketExistsArgs, ct);
                        if (exists)
                        {
                            _logger.LogInformation("[{Provider}] Bucket {BucketName} now exists (may have been created concurrently)", ProviderName, bucketName);
                            return; // Bucket exists now, we're good
                        }
                    }
                    catch
                    {
                        // Ignore check errors, we'll throw the original creation error
                    }

                    _logger.LogError(createEx,
                        "[{Provider}] Failed to create bucket {BucketName}. Ensure the bucket exists in the dashboard or credentials have admin read/write permissions.",
                        ProviderName, bucketName);
                    throw new InvalidOperationException(
                        $"Bucket '{bucketName}' does not exist and could not be created. Please create it manually or grant admin read/write permissions.",
                        createEx);
                }
            }
            else
            {
                _logger.LogDebug("[{Provider}] Bucket {BucketName} already exists", ProviderName, bucketName);
            }
        }
        catch (InvalidOperationException)
        {
            // Re-throw InvalidOperationException (our custom error)
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Provider}] Failed to check if bucket exists: {BucketName}", ProviderName, bucketName);
            throw new InvalidOperationException(
                $"Could not verify bucket '{bucketName}' exists. Please check your storage configuration and ensure the bucket exists.",
                ex);
        }
    }

    public async Task<string> UploadFileAsync(
        Stream stream,
        string bucketName,
        string objectName,
        string contentType,
        CancellationToken ct = default)
    {
        try
        {
            var fullBucketName = GetFullBucketName(bucketName);
            var fullObjectKey = GetObjectKey(bucketName, objectName);

            _logger.LogInformation(
                "[{Provider}] Uploading file: logicalBucket={LogicalBucket}, fullBucket={FullBucket}, objectKey={ObjectKey}",
                ProviderName, bucketName, fullBucketName, fullObjectKey);

            // Ensure bucket exists
            await EnsureBucketExistsAsync(fullBucketName, ct);

            // Reset stream position
            stream.Position = 0;

            // Upload file
            var putObjectArgs = new PutObjectArgs()
                .WithBucket(fullBucketName)
                .WithObject(fullObjectKey)
                .WithStreamData(stream)
                .WithObjectSize(stream.Length)
                .WithContentType(contentType);

            await _minioClient.PutObjectAsync(putObjectArgs, ct);

            _logger.LogInformation(
                "[{Provider}] Uploaded file: {BucketName}/{ObjectName} ({Size} bytes)",
                ProviderName,
                fullBucketName,
                fullObjectKey,
                stream.Length);

            return BuildPublicUrl(_options, fullBucketName, fullObjectKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Provider}] Error uploading file: {BucketName}/{ObjectName}", ProviderName, bucketName, objectName);
            throw;
        }
    }

    /// <summary>
    /// Builds the public URL for a stored object. If a CDN endpoint is configured (e.g. DigitalOcean
    /// Spaces CDN) it is already scoped to the bucket via its hostname, so no bucket segment is added
    /// to the path; otherwise the origin endpoint is used with the bucket in the path. Endpoints are
    /// normalized with the same rules used at provider registration so the two cannot drift (#42).
    /// Static and public so the branching is testable without a live storage client.
    /// </summary>
    public static string BuildPublicUrl(ObjectStorageProviderOptions options, string fullBucketName, string fullObjectKey)
    {
        var protocol = options.UseSSL ? "https" : "http";

        if (ObjectStorageEndpoint.TryNormalize(options.CdnEndpoint, out var cleanCdn))
            return $"{protocol}://{cleanCdn}/{fullObjectKey}";

        if (ObjectStorageEndpoint.TryNormalize(options.Endpoint, out var cleanEndpoint))
            return $"{protocol}://{cleanEndpoint}/{fullBucketName}/{fullObjectKey}";

        // Not reachable through normal registration, which rejects a blank endpoint outright. Throwing
        // beats returning "https:///bucket/key", which would be stored as a broken link.
        throw new InvalidOperationException(
            "No usable Endpoint or CdnEndpoint is configured; cannot build a public URL.");
    }

    public async Task DeleteFileAsync(string bucketName, string objectName, CancellationToken ct = default)
    {
        try
        {
            var fullBucketName = GetFullBucketName(bucketName);
            var fullObjectKey = GetObjectKey(bucketName, objectName);

            var removeObjectArgs = new RemoveObjectArgs()
                .WithBucket(fullBucketName)
                .WithObject(fullObjectKey);

            await _minioClient.RemoveObjectAsync(removeObjectArgs, ct);

            _logger.LogInformation("[{Provider}] Deleted file: {BucketName}/{ObjectName}", ProviderName, fullBucketName, fullObjectKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Provider}] Error deleting file: {BucketName}/{ObjectName}", ProviderName, bucketName, objectName);
            throw;
        }
    }

    public async Task<bool> FileExistsAsync(string bucketName, string objectName, CancellationToken ct = default)
    {
        try
        {
            var fullBucketName = GetFullBucketName(bucketName);
            var fullObjectKey = GetObjectKey(bucketName, objectName);

            var statObjectArgs = new StatObjectArgs()
                .WithBucket(fullBucketName)
                .WithObject(fullObjectKey);

            await _minioClient.StatObjectAsync(statObjectArgs, ct);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<string> GetPresignedUrlAsync(
        string bucketName,
        string objectName,
        int expiresInSeconds = 3600,
        CancellationToken ct = default)
    {
        try
        {
            var fullBucketName = GetFullBucketName(bucketName);
            var fullObjectKey = GetObjectKey(bucketName, objectName);

            var presignedGetObjectArgs = new PresignedGetObjectArgs()
                .WithBucket(fullBucketName)
                .WithObject(fullObjectKey)
                .WithExpiry(expiresInSeconds);

            var url = await _minioClient.PresignedGetObjectAsync(presignedGetObjectArgs);
            return url;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Provider}] Error generating presigned URL: {BucketName}/{ObjectName}", ProviderName, bucketName, objectName);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string> GetPresignedUrlWithFullKeyAsync(
        string fullBucketName,
        string fullObjectKey,
        int expiresInSeconds = 3600,
        CancellationToken ct = default)
    {
        try
        {
            var presignedGetObjectArgs = new PresignedGetObjectArgs()
                .WithBucket(fullBucketName)
                .WithObject(fullObjectKey)
                .WithExpiry(expiresInSeconds);

            var url = await _minioClient.PresignedGetObjectAsync(presignedGetObjectArgs);
            return url;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Provider}] Error generating presigned URL (full key): {BucketName}/{ObjectKey}", ProviderName, fullBucketName, fullObjectKey);
            throw;
        }
    }
}
