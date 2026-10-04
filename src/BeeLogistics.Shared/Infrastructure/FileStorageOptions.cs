namespace BeeLogistics.Shared.Infrastructure;

/// <summary>
/// Selects which registered <see cref="Abstractions.IFileStorageProvider"/> is active.
/// Read via IOptionsMonitor so changing this value in config takes effect on the next
/// file operation without an app restart.
/// </summary>
public class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    /// <summary>"Local" | "S3" | "DigitalOcean" (case-insensitive). Falls back to "NoOp" if unset/unrecognized/unconfigured.</summary>
    public string Provider { get; set; } = "Local";
}
