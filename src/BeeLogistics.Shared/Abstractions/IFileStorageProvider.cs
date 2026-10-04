namespace BeeLogistics.Shared.Abstractions;

/// <summary>
/// A concrete file storage strategy, named so <see cref="IFileStorageProviderFactory"/> can
/// select it by the active "FileStorage:Provider" config value (e.g. "Local", "S3", "DigitalOcean", "NoOp").
/// </summary>
public interface IFileStorageProvider : IFileStorageService
{
    string ProviderName { get; }
}
