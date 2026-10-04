using BeeLogistics.Shared.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using nClam;

namespace BeeLogistics.Shared.Infrastructure;

/// <summary>
/// ClamAV virus scanner implementation
/// Scans uploaded files for malware and viruses using ClamAV daemon
/// </summary>
public class ClamAVScanner : IVirusScanner
{
    private readonly ILogger<ClamAVScanner> _logger;
    private readonly string? _host;
    private readonly int _port;
    private readonly bool _enabled;
    private readonly int _timeoutSeconds;

    public ClamAVScanner(IConfiguration configuration, ILogger<ClamAVScanner> logger)
    {
        _logger = logger;
        var clamAvSection = configuration.GetSection("ClamAV");
        _enabled = clamAvSection.GetValue<bool>("Enabled", false);
        _host = clamAvSection.GetValue<string>("Host");
        _port = clamAvSection.GetValue<int>("Port", 3310);
        _timeoutSeconds = clamAvSection.GetValue<int>("TimeoutSeconds", 30);

        if (_enabled && string.IsNullOrEmpty(_host))
        {
            _logger.LogWarning("ClamAV is enabled but Host is not configured. Virus scanning will be disabled.");
            _enabled = false;
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrEmpty(_host))
            return false;

        try
        {
            var client = CreateClient();
            var result = await client.PingAsync();
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ClamAV server is not available");
            return false;
        }
    }

    public async Task<VirusScanResult> ScanFileAsync(Stream fileStream, string fileName, CancellationToken ct = default)
    {
        // If ClamAV is disabled, allow the file (for development/testing)
        if (!_enabled)
        {
            _logger.LogDebug("ClamAV is disabled, skipping virus scan for file: {FileName}", fileName);
            return VirusScanResult.Clean();
        }

        if (string.IsNullOrEmpty(_host))
        {
            _logger.LogWarning("ClamAV host is not configured, allowing file: {FileName}", fileName);
            return VirusScanResult.Clean();
        }

        try
        {
            // Check if ClamAV is available before attempting scan
            var isAvailable = await IsAvailableAsync(ct);
            if (!isAvailable)
            {
                _logger.LogWarning("ClamAV server is not available for file: {FileName}", fileName);
                return VirusScanResult.Error("ClamAV server is not available. Please ensure the ClamAV daemon is running.");
            }

            var client = CreateClient();
            
            // Reset stream position to beginning
            if (fileStream.CanSeek)
            {
                fileStream.Position = 0;
            }

            // Scan the file stream with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
            
            var scanResult = await client.SendAndScanFileAsync(fileStream, timeoutCts.Token);

            // Reset stream position after scan
            if (fileStream.CanSeek)
            {
                fileStream.Position = 0;
            }

            switch (scanResult.Result)
            {
                case ClamScanResults.Clean:
                    _logger.LogInformation("File scanned clean: {FileName}", fileName);
                    return VirusScanResult.Clean();

                case ClamScanResults.VirusDetected:
                    var virusName = scanResult.InfectedFiles?.FirstOrDefault()?.VirusName ?? "Unknown";
                    _logger.LogWarning("Virus detected in file {FileName}: {VirusName}", fileName, virusName);
                    return VirusScanResult.Infected(virusName);

                case ClamScanResults.Error:
                    var errorMsg = scanResult.RawResult ?? "Unknown error during virus scan";
                    _logger.LogError("Error scanning file {FileName}: {Error}", fileName, errorMsg);
                    // On error, we should reject the file for security (fail-secure)
                    return VirusScanResult.Error($"Virus scan failed: {errorMsg}");

                case ClamScanResults.Unknown:
                default:
                    _logger.LogWarning("Unknown scan result for file {FileName}, rejecting for security", fileName);
                    return VirusScanResult.Error("Unknown virus scan result");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Virus scan was cancelled or timed out for file: {FileName}", fileName);
            return VirusScanResult.Error("Virus scan was cancelled or timed out");
        }
        catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionAborted || 
                                                           ex.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset)
        {
            _logger.LogError(ex, "Connection to ClamAV was aborted while scanning file: {FileName}. This may indicate ClamAV daemon is not fully initialized or crashed.", fileName);
            return VirusScanResult.Error("Connection to ClamAV was lost. Please ensure ClamAV daemon is running and fully initialized.");
        }
        catch (System.IO.IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException socketEx && 
                                               (socketEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionAborted ||
                                                socketEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset))
        {
            _logger.LogError(ex, "Connection to ClamAV was aborted while sending file: {FileName}. ClamAV daemon may have closed the connection.", fileName);
            return VirusScanResult.Error("Connection to ClamAV was lost during file transfer. Please check ClamAV daemon status.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during virus scan for file {FileName}", fileName);
            // Fail-secure: reject file if scanner is unavailable
            return VirusScanResult.Error($"Virus scan failed: {ex.Message}");
        }
    }

    private ClamClient CreateClient()
    {
        if (string.IsNullOrEmpty(_host))
        {
            throw new InvalidOperationException("ClamAV host is not configured");
        }
        var client = new ClamClient(_host, _port);
        // Set timeout if supported by nClam version
        return client;
    }
}
