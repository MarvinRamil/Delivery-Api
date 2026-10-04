using BeeLogistics.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Shared.Infrastructure;

/// <summary>
/// Selects the active IFileStorageProvider by name from live config (FileStorage:Provider).
/// Mirrors the Payments module's IPaymentGatewayFactory: every configured provider registers
/// simultaneously, and this factory just picks which one is "active" - so switching never
/// requires re-registering DI or restarting the app.
/// </summary>
public class FileStorageProviderFactory : IFileStorageProviderFactory
{
    private const string FallbackProviderName = "NoOp";

    private readonly Dictionary<string, IFileStorageProvider> _providers;
    private readonly IOptionsMonitor<FileStorageOptions> _options;
    private readonly ILogger<FileStorageProviderFactory> _logger;

    public FileStorageProviderFactory(
        IEnumerable<IFileStorageProvider> providers,
        IOptionsMonitor<FileStorageOptions> options,
        ILogger<FileStorageProviderFactory> logger)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
        _options = options;
        _logger = logger;
    }

    public IFileStorageService GetActive()
    {
        var name = _options.CurrentValue.Provider;
        if (!string.IsNullOrWhiteSpace(name) && _providers.TryGetValue(name, out var provider))
            return provider;

        // Error, not Warning: the configuration explicitly asked for this provider, so substituting
        // NoOp means every upload from here on reports success and stores nothing. That went
        // unnoticed at Warning level in issue #42.
        _logger.LogError(
            "FileStorage:Provider '{Provider}' is not registered/configured, falling back to {Fallback} - " +
            "file operations will appear to succeed but nothing will be stored",
            name, FallbackProviderName);
        return _providers[FallbackProviderName];
    }

    public IFileStorageService? Get(string providerName)
    {
        return _providers.TryGetValue(providerName, out var provider) ? provider : null;
    }
}
