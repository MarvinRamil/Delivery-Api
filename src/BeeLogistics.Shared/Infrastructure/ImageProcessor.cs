using BeeLogistics.Shared.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace BeeLogistics.Shared.Infrastructure;

/// <summary>
/// Image processing service using ImageSharp
/// Converts images to WebP format and resizes them to standard dimensions
/// </summary>
public class ImageProcessor : IImageProcessor
{
    private readonly ILogger<ImageProcessor> _logger;
    private readonly int _maxWidth;
    private readonly int _maxHeight;
    private readonly int _webpQuality;
    private readonly ResizeMode _resizeMode;

    public ImageProcessor(IConfiguration configuration, ILogger<ImageProcessor> logger)
    {
        _logger = logger;
        var imageProcessingSection = configuration.GetSection("ImageProcessing");
        _maxWidth = imageProcessingSection.GetValue<int>("MaxWidth", 1920);
        _maxHeight = imageProcessingSection.GetValue<int>("MaxHeight", 1080);
        _webpQuality = imageProcessingSection.GetValue<int>("WebpQuality", 85); // 0-100, 85 is good balance
        _resizeMode = imageProcessingSection.GetValue<string>("ResizeMode", "Max") switch
        {
            "Max" => ResizeMode.Max,
            "Crop" => ResizeMode.Crop,
            "Pad" => ResizeMode.Pad,
            "BoxPad" => ResizeMode.BoxPad,
            "Min" => ResizeMode.Min,
            "Stretch" => ResizeMode.Stretch,
            _ => ResizeMode.Max
        };
    }

    public async Task<ImageProcessingResult> ProcessImageAsync(Stream inputStream, Stream outputStream, CancellationToken ct = default)
    {
        var originalSize = inputStream.Length;

        try
        {
            // Reset stream position
            if (inputStream.CanSeek)
            {
                inputStream.Position = 0;
            }

            // Load image
            using var image = await Image.LoadAsync(inputStream, ct);

            var originalWidth = image.Width;
            var originalHeight = image.Height;

            // Calculate new dimensions maintaining aspect ratio
            var (newWidth, newHeight) = CalculateDimensions(originalWidth, originalHeight, _maxWidth, _maxHeight);

            // Resize image if needed
            if (newWidth != originalWidth || newHeight != originalHeight)
            {
                _logger.LogInformation(
                    "Resizing image from {OriginalWidth}x{OriginalHeight} to {NewWidth}x{NewHeight}",
                    originalWidth, originalHeight, newWidth, newHeight);

                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(newWidth, newHeight),
                    Mode = _resizeMode,
                    Sampler = KnownResamplers.Lanczos3 // High quality resampling
                }));
            }

            // Configure WebP encoder with quality settings
            var encoder = new WebpEncoder
            {
                Quality = _webpQuality,
                Method = WebpEncodingMethod.BestQuality // Slower but better compression
            };

            // Save as WebP
            await image.SaveAsync(outputStream, encoder, ct);

            var processedSize = outputStream.Length;

            _logger.LogInformation(
                "Image processed: {OriginalSize} bytes -> {ProcessedSize} bytes ({CompressionRatio:F1}% reduction), {Width}x{Height}",
                originalSize, processedSize, 
                originalSize > 0 ? (1.0 - (double)processedSize / originalSize) * 100.0 : 0,
                newWidth, newHeight);

            return ImageProcessingResult.SuccessResult(originalSize, processedSize, newWidth, newHeight);
        }
        catch (UnknownImageFormatException ex)
        {
            _logger.LogError(ex, "Unsupported image format");
            return ImageProcessingResult.Error($"Unsupported image format: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing image");
            return ImageProcessingResult.Error($"Image processing failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Calculate new dimensions maintaining aspect ratio
    /// </summary>
    private static (int width, int height) CalculateDimensions(int originalWidth, int originalHeight, int maxWidth, int maxHeight)
    {
        // If image is already smaller than max dimensions, return original
        if (originalWidth <= maxWidth && originalHeight <= maxHeight)
        {
            return (originalWidth, originalHeight);
        }

        // Calculate scaling factor to fit within max dimensions
        var widthRatio = (double)maxWidth / originalWidth;
        var heightRatio = (double)maxHeight / originalHeight;
        var ratio = Math.Min(widthRatio, heightRatio);

        var newWidth = (int)(originalWidth * ratio);
        var newHeight = (int)(originalHeight * ratio);

        // Ensure dimensions are even (better for WebP encoding)
        newWidth = newWidth % 2 == 0 ? newWidth : newWidth - 1;
        newHeight = newHeight % 2 == 0 ? newHeight : newHeight - 1;

        return (newWidth, newHeight);
    }
}
