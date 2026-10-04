namespace BeeLogistics.Shared.Abstractions;

/// <summary>
/// Interface for virus scanning service
/// Provides abstraction for antivirus scanning of uploaded files
/// </summary>
public interface IVirusScanner
{
    /// <summary>
    /// Scans a file stream for viruses/malware
    /// </summary>
    /// <param name="fileStream">The file stream to scan</param>
    /// <param name="fileName">The name of the file (for logging purposes)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Scan result indicating if file is clean or infected</returns>
    Task<VirusScanResult> ScanFileAsync(Stream fileStream, string fileName, CancellationToken ct = default);

    /// <summary>
    /// Checks if the virus scanner is available and configured
    /// </summary>
    /// <returns>True if scanner is available, false otherwise</returns>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

/// <summary>
/// Result of a virus scan operation
/// </summary>
public class VirusScanResult
{
    public bool IsClean { get; set; }
    public bool IsInfected => !IsClean;
    public string? VirusName { get; set; }
    public string? ErrorMessage { get; set; }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public static VirusScanResult Clean() => new() { IsClean = true };
    
    public static VirusScanResult Infected(string virusName) => new() 
    { 
        IsClean = false, 
        VirusName = virusName 
    };
    
    public static VirusScanResult Error(string errorMessage) => new() 
    { 
        IsClean = false, 
        ErrorMessage = errorMessage 
    };
}
