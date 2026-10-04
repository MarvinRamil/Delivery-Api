using BeeLogistics.Shared.Infrastructure.Security;
using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Shared.Infrastructure.Configuration;

/// <summary>
/// Loads application secrets from a HashiCorp Vault KV v2 secret into <see cref="IConfiguration"/>.
/// Secret keys use the env-var convention with "__" as the section separator (e.g.
/// "ConnectionStrings__DefaultConnection"), which is translated to the ":" config key form.
///
/// Added as the highest-precedence source so Vault values override appsettings.json / env defaults.
/// Fails fast if Vault is unreachable or the secret is missing — the app must never boot with
/// secrets silently absent.
/// </summary>
public sealed class VaultConfigurationSource : IConfigurationSource
{
    private readonly VaultOptions _options;
    private readonly string _secretPath;

    public VaultConfigurationSource(VaultOptions options, string secretPath)
    {
        _options = options;
        _secretPath = secretPath;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new VaultConfigurationProvider(_options, _secretPath);
}

public sealed class VaultConfigurationProvider : ConfigurationProvider
{
    private readonly VaultOptions _options;
    private readonly string _secretPath;

    public VaultConfigurationProvider(VaultOptions options, string secretPath)
    {
        _options = options;
        _secretPath = secretPath;
    }

    public override void Load()
    {
        var client = VaultClientFactory.Create(_options);

        // Startup-time blocking call. There is no synchronization context during host build, so
        // blocking here is safe and keeps the IConfigurationProvider.Load contract (sync).
        var secret = client.V1.Secrets.KeyValue.V2
            .ReadSecretAsync(path: _secretPath, mountPoint: _options.MountPath)
            .GetAwaiter().GetResult();

        var data = secret.Data.Data;
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in data)
        {
            // "ConnectionStrings__DefaultConnection" -> "ConnectionStrings:DefaultConnection"
            var key = kvp.Key.Replace("__", ConfigurationPath.KeyDelimiter);
            result[key] = kvp.Value?.ToString();
        }

        Data = result;
    }
}
