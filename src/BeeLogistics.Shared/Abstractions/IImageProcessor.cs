namespace BeeLogistics.Shared.Abstractions;

/// <summary>
/// Interface for image processing service
/// Handles image conversion, resizing, and optimization
/// </summary>
public interface IImageProcessor
{
    /// <summary>
    /// Processes an image: converts to WebP, resizes to standard dimensions, and optimizes
    /// </summary>
    /// <param name="inputStream">Input image stream (JPG, PNG, etc.)</param>
    /// <param name="outputStream">Output stream for processed WebP image</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Processing result with file size information</returns>
    Task<ImageProcessingResult> ProcessImageAsync(Stream inputStream, Stream outputStream, CancellationToken ct = default);
}

/// <summary>
/// Result of image processing operation
/// </summary>
public class ImageProcessingResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public long OriginalSizeBytes { get; set; }
    public long ProcessedSizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double CompressionRatio => OriginalSizeBytes > 0 ? (1.0 - (double)ProcessedSizeBytes / OriginalSizeBytes) * 100.0 : 0;

    public static ImageProcessingResult SuccessResult(long originalSize, long processedSize, int width, int height) => new()
    {
        Success = true,
        OriginalSizeBytes = originalSize,
        ProcessedSizeBytes = processedSize,
        Width = width,
        Height = height
    };

    public static ImageProcessingResult Error(string errorMessage) => new()
    {
        Success = false,
        ErrorMessage = errorMessage
    };
}
