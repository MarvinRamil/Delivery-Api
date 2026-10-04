using BeeLogistics.Shared.Infrastructure.Security;
using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Shared.Infrastructure.Configuration;

public static class VaultConfigurationExtensions
{
    /// <summary>
    /// Adds a Vault KV v2 secret as a configuration source. Call after the file/env sources so
    /// Vault-provided secrets take precedence.
    /// </summary>
    public static IConfigurationBuilder AddVaultSecrets(this IConfigurationBuilder builder, VaultOptions options, string secretPath)
        => builder.Add(new VaultConfigurationSource(options, secretPath));
}
