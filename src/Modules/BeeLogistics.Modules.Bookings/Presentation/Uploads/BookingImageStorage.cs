using BeeLogistics.Shared.Abstractions;
using Microsoft.AspNetCore.Http;

namespace BeeLogistics.Modules.Bookings.Presentation.Uploads;

/// <summary>
/// Writes booking images to the deliveries bucket, re-encoded to WebP.
/// </summary>
/// <remarks>
/// Extracted from BookingsController so the create endpoints and the item-image upload
/// endpoint — now in separate controllers — keep storing item images the same way.
/// </remarks>
internal sealed class BookingImageStorage
{
    /// <summary>Item images and proof-of-delivery images share this bucket.</summary>
    internal const string DeliveriesBucket = "deliveries";

    private readonly IImageProcessor _imageProcessor;
    private readonly IFileStorageService _fileStorageService;

    public BookingImageStorage(IImageProcessor imageProcessor, IFileStorageService fileStorageService)
    {
        _imageProcessor = imageProcessor;
        _fileStorageService = fileStorageService;
    }

    /// <summary>
    /// Saves booking item image to storage (R2 or local). Same folder as POD: deliveries/{bookingId}/.
    /// Converts to WebP. Returns s3:deliveries:objectKey or local path.
    /// </summary>
    public async Task<string> SaveItemImageAsync(IFormFile file, Guid bookingId, CancellationToken ct)
    {
        var objectName = $"{bookingId:N}/item-image.webp";
        await using var inputStream = file.OpenReadStream();
        await using var processedStream = new MemoryStream();
        var processingResult = await _imageProcessor.ProcessImageAsync(inputStream, processedStream, ct);
        if (!processingResult.Success)
            throw new InvalidOperationException($"Image processing failed: {processingResult.ErrorMessage}");
        processedStream.Position = 0;
        var pathOrUrl = await _fileStorageService.UploadFileAsync(processedStream, DeliveriesBucket, objectName, "image/webp", ct);
        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return $"s3:{DeliveriesBucket}:{objectName}";
        return pathOrUrl;
    }
}
