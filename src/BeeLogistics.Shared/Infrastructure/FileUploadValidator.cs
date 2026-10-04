namespace BeeLogistics.Shared.Infrastructure;

/// <summary>
/// Centralized file upload validation: extension, Content-Type, magic bytes (file signatures), and optional size limit.
/// Use from controllers to ensure consistent validation and one place to update allowed types.
/// </summary>
public static class FileUploadValidator
{
    /// <summary>
    /// Validates file by extension, Content-Type, and actual file content (magic bytes).
    /// Optionally enforces max size. Returns true if valid.
    /// </summary>
    public static async Task<bool> ValidateAsync(
        Microsoft.AspNetCore.Http.IFormFile? file,
        FileUploadValidationOptions options,
        CancellationToken ct = default)
    {
        if (file == null || file.Length == 0)
            return false;

        if (options.MaxSizeBytes.HasValue && file.Length > options.MaxSizeBytes.Value)
            return false;

        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrEmpty(fileName))
            return false;

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) || !options.AllowedExtensions.Contains(ext))
            return false;

        if (!options.AllowedContentTypes.Contains(file.ContentType))
            return false;

        if (options.FileSignatures == null || options.FileSignatures.Count == 0)
            return true;

        if (!options.FileSignatures.TryGetValue(ext, out var signatures))
            return false;

        await using var stream = file.OpenReadStream();
        var buffer = new byte[Math.Min(8, (int)file.Length)];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);

        if (bytesRead < signatures.Max(s => s.Length))
            return false;

        return signatures.Any(signature =>
        {
            if (bytesRead < signature.Length)
                return false;
            for (int i = 0; i < signature.Length; i++)
            {
                if (buffer[i] != signature[i])
                    return false;
            }
            return true;
        });
    }

    /// <summary>
    /// Common image signatures (JPEG, PNG) for use in options.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, byte[][]> ImageSignatures = new Dictionary<string, byte[][]>(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = new[] { new byte[] { 0xFF, 0xD8, 0xFF } },
        [".jpeg"] = new[] { new byte[] { 0xFF, 0xD8, 0xFF } },
        [".png"] = new[] { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } },
    };

    /// <summary>
    /// Image + PDF signatures.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, byte[][]> ImageAndPdfSignatures = new Dictionary<string, byte[][]>(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = new[] { new byte[] { 0xFF, 0xD8, 0xFF } },
        [".jpeg"] = new[] { new byte[] { 0xFF, 0xD8, 0xFF } },
        [".png"] = new[] { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } },
        [".pdf"] = new[] { new byte[] { 0x25, 0x50, 0x44, 0x46 } }, // %PDF
    };

    /// <summary>
    /// Preset: images only (jpg, jpeg, png). Pass maxSizeBytes to enforce size limit.
    /// </summary>
    public static FileUploadValidationOptions ImageOnlyOptions(long? maxSizeBytes = null) => new(
        AllowedExtensions: new[] { ".jpg", ".jpeg", ".png" },
        AllowedContentTypes: new[] { "image/jpeg", "image/png" },
        FileSignatures: new Dictionary<string, byte[][]>(ImageSignatures),
        MaxSizeBytes: maxSizeBytes);

    /// <summary>
    /// Preset: images + PDF (e.g. driver documents). Pass maxSizeBytes to enforce size limit.
    /// </summary>
    public static FileUploadValidationOptions ImageAndPdfOptions(long? maxSizeBytes = null) => new(
        AllowedExtensions: new[] { ".jpg", ".jpeg", ".png", ".pdf" },
        AllowedContentTypes: new[] { "image/jpeg", "image/png", "application/pdf" },
        FileSignatures: new Dictionary<string, byte[][]>(ImageAndPdfSignatures),
        MaxSizeBytes: maxSizeBytes);
}

/// <summary>
/// Options for file upload validation.
/// </summary>
/// <param name="AllowedExtensions">Allowed file extensions (e.g. .jpg, .png).</param>
/// <param name="AllowedContentTypes">Allowed MIME types (e.g. image/jpeg).</param>
/// <param name="FileSignatures">Magic bytes per extension; if null/empty, signature check is skipped.</param>
/// <param name="MaxSizeBytes">Optional max file size in bytes.</param>
public record FileUploadValidationOptions(
    string[] AllowedExtensions,
    string[] AllowedContentTypes,
    IReadOnlyDictionary<string, byte[][]>? FileSignatures,
    long? MaxSizeBytes = null);
