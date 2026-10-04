using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.AppRole;

namespace BeeLogistics.Shared.Infrastructure.Security;

/// <summary>
/// Builds an authenticated Vault client from <see cref="VaultOptions"/> (AppRole auth, with optional
/// TLS-verification bypass for self-signed dev certs). Shared by <see cref="VaultKeyProvider"/> and
/// the Vault configuration provider so auth/TLS behaviour stays consistent.
/// </summary>
public static class VaultClientFactory
{
    public static IVaultClient Create(VaultOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Address))
            throw new InvalidOperationException("Vault:Address is not configured.");
        if (string.IsNullOrWhiteSpace(options.RoleId) || string.IsNullOrWhiteSpace(options.SecretId))
            throw new InvalidOperationException("Vault RoleId/SecretId are not configured (set Vault__RoleId / Vault__SecretId).");

        IAuthMethodInfo authMethod = new AppRoleAuthMethodInfo(options.RoleId, options.SecretId);
        var settings = new VaultClientSettings(options.Address, authMethod);

        if (options.SkipTlsVerify)
        {
            settings.PostProcessHttpClientHandlerAction = handler =>
            {
                if (handler is HttpClientHandler h)
                    h.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            };
        }

        return new VaultClient(settings);
    }
}
