namespace BeeLogistics.Shared.Infrastructure.Security;

/// <summary>
/// Configuration for fetching encryption keys from HashiCorp Vault (KV v2 + AppRole).
/// Bound from the "Vault" configuration section. RoleId/SecretId are supplied via
/// environment variables (Vault__RoleId / Vault__SecretId) and never committed.
/// </summary>
public class VaultOptions
{
    public const string SectionName = "Vault";

    /// <summary>Vault server URL, e.g. https://10.10.10.117:8200</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>KV v2 mount path. We use a dedicated mount: "bee".</summary>
    public string MountPath { get; set; } = "bee";

    /// <summary>Secret path (within the mount) holding the encryption keys, e.g. "dpa/encryption".</summary>
    public string SecretPath { get; set; } = "dpa/encryption";

    /// <summary>Secret path (within the mount) holding general application secrets, e.g. "config".</summary>
    public string ConfigSecretPath { get; set; } = "config";

    /// <summary>Secret path (within the mount) holding driver-facing config proxied to the mobile app, e.g. "driver-config".</summary>
    public string DriverConfigSecretPath { get; set; } = "driver-config";

    /// <summary>AppRole role id.</summary>
    public string RoleId { get; set; } = string.Empty;

    /// <summary>AppRole secret id.</summary>
    public string SecretId { get; set; } = string.Empty;

    /// <summary>
    /// Skip TLS certificate validation. DEV ONLY (self-signed Vault cert). Must be false in prod.
    /// </summary>
    public bool SkipTlsVerify { get; set; }

    /// <summary>Key name in the secret holding the base64 AES-256 master key (version 1).</summary>
    public string MasterKeyName { get; set; } = "master-key";

    /// <summary>Key name in the secret holding the base64 HMAC blind-index key.</summary>
    public string BlindIndexKeyName { get; set; } = "blind-index-key";
}
