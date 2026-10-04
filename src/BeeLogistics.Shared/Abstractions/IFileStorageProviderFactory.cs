namespace BeeLogistics.Shared.Abstractions;

/// <summary>
/// Resolves the currently active <see cref="IFileStorageProvider"/> based on live config
/// (FileStorage:Provider), so switching providers doesn't require an app restart.
/// </summary>
public interface IFileStorageProviderFactory
{
    /// <summary>The provider named by the current FileStorage:Provider value, or the "NoOp" fallback if unavailable.</summary>
    IFileStorageService GetActive();

    /// <summary>Look up a specific provider by name, or null if it isn't registered/configured.</summary>
    IFileStorageService? Get(string providerName);
}
