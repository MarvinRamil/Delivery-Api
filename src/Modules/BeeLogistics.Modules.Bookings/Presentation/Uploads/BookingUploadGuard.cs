using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Bookings.Presentation.Uploads;

/// <summary>
/// The rejection messages a single upload site returns. Every upload endpoint had its own
/// wording for the same three checks, and those strings are part of what clients display,
/// so they are supplied per call site rather than unified here.
/// </summary>
internal sealed record UploadGuardMessages(
    string TooLarge,
    string WrongType,
    string Infected,
    string ScanFailed,
    bool IncludeVirusName = true,
    string ScanErrorPrefix = "File upload rejected: ");

/// <summary>
/// Size, file-type and virus checks shared by the booking upload endpoints.
/// </summary>
/// <remarks>
/// Extracted from five near-identical copies in <c>BookingsController</c>. The copies did not
/// agree, and this preserves those differences rather than resolving them:
/// <list type="bullet">
/// <item>every message is supplied by the caller (see <see cref="UploadGuardMessages"/>);</item>
/// <item>the POD signature upload passes <c>scanForViruses: false</c> - it has never been virus
/// scanned, and adding a scan would give a path the driver app hits on every completed delivery
/// a failure mode it does not have today. Tracked separately, not changed here.</item>
/// </list>
/// Returns the rejection message, or null when the file passes. Callers render their own
/// response so the wire shape stays exactly as it was.
/// </remarks>
internal sealed class BookingUploadGuard
{
    private readonly IVirusScanner _virusScanner;
    private readonly IConfiguration _configuration;
    private readonly long _maxFileSizeBytes;
    private readonly ILogger _logger;

    public BookingUploadGuard(
        IVirusScanner virusScanner,
        IConfiguration configuration,
        long maxFileSizeBytes,
        ILogger logger)
    {
        _virusScanner = virusScanner;
        _configuration = configuration;
        _maxFileSizeBytes = maxFileSizeBytes;
        _logger = logger;
    }

    public double MaxFileSizeMb => _maxFileSizeBytes / (1024.0 * 1024.0);

    public async Task<string?> CheckAsync(
        IFormFile file,
        UploadGuardMessages messages,
        CancellationToken ct,
        bool scanForViruses = true)
    {
        // SECURITY: File size validation
        if (file.Length > _maxFileSizeBytes)
            return messages.TooLarge;

        // SECURITY: File extension, content type, and magic-byte validation (centralized)
        if (!await FileUploadValidator.ValidateAsync(file, FileUploadValidator.ImageOnlyOptions(_maxFileSizeBytes), ct))
            return messages.WrongType;

        if (!scanForViruses)
            return null;

        // SECURITY: Virus scanning with ClamAV
        try
        {
            using var fileStream = file.OpenReadStream();
            var scanResult = await _virusScanner.ScanFileAsync(fileStream, file.FileName, ct);

            if (scanResult.IsInfected)
            {
                var virusInfo = messages.IncludeVirusName && !string.IsNullOrEmpty(scanResult.VirusName)
                    ? $" Detected threat: {scanResult.VirusName}"
                    : "";
                return $"{messages.Infected}{virusInfo}";
            }

            if (scanResult.HasError)
            {
                // Fail-secure: Reject file if scanner is unavailable or errors occur
                if (RequireVirusScan)
                    return $"{messages.ScanErrorPrefix}{scanResult.ErrorMessage}";

                // If scanning is optional, log warning but allow file
                _logger.LogWarning("Virus scan failed but allowing file: {Error}", scanResult.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            // Fail-secure: Reject file if scanning fails
            if (RequireVirusScan)
                return messages.ScanFailed;

            _logger.LogWarning(ex, "Virus scan threw but allowing file");
        }

        return null;
    }

    private bool RequireVirusScan => _configuration.GetValue<bool>("FileUpload:RequireVirusScan", true);
}
