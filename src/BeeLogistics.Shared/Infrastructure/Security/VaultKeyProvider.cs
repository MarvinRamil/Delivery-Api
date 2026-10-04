using System.Text.RegularExpressions;
using BeeLogistics.Shared.Abstractions;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Shared.Infrastructure.Security;

/// <summary>
/// Loads AES master key(s) and the HMAC blind-index key from HashiCorp Vault (KV v2) using
/// AppRole auth, once at startup. Keys are cached in memory for the process lifetime.
///
/// Supports future key rotation with zero code/data migration: the secret may hold
/// "master-key" (version 1) and additional "master-key-vN" entries. The highest version is
/// used to encrypt new values; every version remains available to decrypt old values.
/// </summary>
public sealed class VaultKeyProvider : IEncryptionKeyProvider
{
    private const int RequiredKeyBytes = 32; // AES-256 / HMAC-SHA256

    private static readonly Regex VersionedMasterKeyRegex =
        new(@"^master-key-v(?<v>\d{1,3})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly VaultOptions _options;
    private readonly ILogger<VaultKeyProvider>? _logger;

    private readonly Dictionary<byte, byte[]> _masterKeys = new();
    private byte[] _blindIndexKey = Array.Empty<byte>();
    private bool _loaded;

    public VaultKeyProvider(VaultOptions options, ILogger<VaultKeyProvider>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public byte CurrentMasterKeyVersion { get; private set; }

    public byte[] GetMasterKey(byte version)
    {
        EnsureLoaded();
        if (_masterKeys.TryGetValue(version, out var key)) return key;
        throw new InvalidOperationException(
            $"No encryption master key for version {version} is available from Vault. " +
            "Encrypted data cannot be read until the matching key is restored.");
    }

    public byte[] BlindIndexKey
    {
        get { EnsureLoaded(); return _blindIndexKey; }
    }

    /// <summary>
    /// Authenticates to Vault and loads all key material. Call once at startup before building
    /// the encryption service. Throws (fail-fast) on any misconfiguration so the app never starts
    /// silently unable to read PII.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_options.SkipTlsVerify)
            _logger?.LogWarning("Vault TLS verification is DISABLED (SkipTlsVerify=true). Do not use this in production.");

        var client = VaultClientFactory.Create(_options);

        var secret = await client.V1.Secrets.KeyValue.V2
            .ReadSecretAsync(path: _options.SecretPath, mountPoint: _options.MountPath)
            .ConfigureAwait(false);

        var data = secret.Data.Data;

        // Master keys: "master-key" => v1, "master-key-vN" => vN.
        foreach (var kvp in data)
        {
            byte version;
            if (kvp.Key.Equals(_options.MasterKeyName, StringComparison.OrdinalIgnoreCase))
            {
                version = 1;
            }
            else
            {
                var m = VersionedMasterKeyRegex.Match(kvp.Key);
                if (!m.Success) continue;
                version = byte.Parse(m.Groups["v"].Value);
            }

            _masterKeys[version] = DecodeKey(kvp.Key, kvp.Value?.ToString());
        }

        if (_masterKeys.Count == 0)
            throw new InvalidOperationException($"Vault secret '{_options.MountPath}/{_options.SecretPath}' has no '{_options.MasterKeyName}'.");

        CurrentMasterKeyVersion = _masterKeys.Keys.Max();

        if (!data.TryGetValue(_options.BlindIndexKeyName, out var biRaw))
            throw new InvalidOperationException($"Vault secret is missing '{_options.BlindIndexKeyName}'.");
        _blindIndexKey = DecodeKey(_options.BlindIndexKeyName, biRaw?.ToString());

        _loaded = true;
        _logger?.LogInformation(
            "Loaded encryption keys from Vault {Mount}/{Path}: {MasterKeyCount} master key version(s), current=v{Current}.",
            _options.MountPath, _options.SecretPath, _masterKeys.Count, CurrentMasterKeyVersion);
    }

    private static byte[] DecodeKey(string name, string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException($"Vault key '{name}' is empty.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64.Trim()); }
        catch (FormatException) { throw new InvalidOperationException($"Vault key '{name}' is not valid base64."); }

        if (bytes.Length != RequiredKeyBytes)
            throw new InvalidOperationException($"Vault key '{name}' must decode to {RequiredKeyBytes} bytes (got {bytes.Length}). Generate with: openssl rand -base64 32");

        return bytes;
    }

    private void EnsureLoaded()
    {
        if (!_loaded)
            throw new InvalidOperationException("VaultKeyProvider.LoadAsync() must be awaited at startup before use.");
    }
}
