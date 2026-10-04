using BeeLogistics.Shared.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Shared.Infrastructure;

/// <summary>
/// Local file system storage service for PoC/testing
/// Stores files in wwwroot/uploads or configured path
/// </summary>
public class LocalFileStorageService : IFileStorageProvider
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalFileStorageService> _logger;
    private readonly string _basePath;
    private readonly string _baseUrl;

    public string ProviderName => "Local";

    public LocalFileStorageService(IConfiguration configuration, ILogger<LocalFileStorageService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        
        // Get base path from config or use default
        var configuredPath = _configuration["FileStorage:LocalPath"];
        _basePath = string.IsNullOrEmpty(configuredPath) 
            ? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads")
            : configuredPath;
        
        // Get base URL from config or use default
        _baseUrl = _configuration["FileStorage:BaseUrl"] ?? "/uploads";
        
        // Ensure directory exists
        if (!Directory.Exists(_basePath))
        {
            Directory.CreateDirectory(_basePath);
            _logger.LogInformation("Created file storage directory: {BasePath}", _basePath);
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
            // Create bucket directory if it doesn't exist
            var bucketPath = Path.Combine(_basePath, bucketName);
            if (!Directory.Exists(bucketPath))
            {
                Directory.CreateDirectory(bucketPath);
            }

            // Full file path
            var filePath = Path.Combine(bucketPath, objectName);
            
            // Ensure directory for object exists (in case objectName contains subdirectories)
            var objectDirectory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(objectDirectory) && !Directory.Exists(objectDirectory))
            {
                Directory.CreateDirectory(objectDirectory);
            }

            // Reset stream position
            stream.Position = 0;

            // Write file
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
            await stream.CopyToAsync(fileStream, ct);
            await fileStream.FlushAsync(ct);

            _logger.LogInformation(
                "Uploaded file to local storage: {BucketName}/{ObjectName} ({Size} bytes)",
                bucketName,
                objectName,
                stream.Length);

            // Return public URL (relative path for now)
            var publicUrl = $"{_baseUrl.TrimEnd('/')}/{bucketName}/{objectName}";
            return publicUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading file to local storage: {BucketName}/{ObjectName}", bucketName, objectName);
            throw;
        }
    }

    public async Task DeleteFileAsync(string bucketName, string objectName, CancellationToken ct = default)
    {
        try
        {
            var filePath = Path.Combine(_basePath, bucketName, objectName);
            
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInformation("Deleted file from local storage: {BucketName}/{ObjectName}", bucketName, objectName);
            }
            else
            {
                _logger.LogWarning("File not found for deletion: {BucketName}/{ObjectName}", bucketName, objectName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting file from local storage: {BucketName}/{ObjectName}", bucketName, objectName);
            throw;
        }
    }

    public async Task<bool> FileExistsAsync(string bucketName, string objectName, CancellationToken ct = default)
    {
        try
        {
            var filePath = Path.Combine(_basePath, bucketName, objectName);
            return File.Exists(filePath);
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
        // For local storage, presigned URLs are not needed - just return the public URL
        return await Task.FromResult($"{_baseUrl.TrimEnd('/')}/{bucketName}/{objectName}");
    }

    /// <inheritdoc />
    public Task<string> GetPresignedUrlWithFullKeyAsync(
        string fullBucketName,
        string fullObjectKey,
        int expiresInSeconds = 3600,
        CancellationToken ct = default)
    {
        // For local storage, return URL path from full bucket/key
        return Task.FromResult($"{_baseUrl.TrimEnd('/')}/{fullBucketName}/{fullObjectKey}");
    }
}
