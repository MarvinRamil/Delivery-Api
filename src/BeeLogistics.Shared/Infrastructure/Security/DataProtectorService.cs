using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BeeLogistics.Shared.Abstractions;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Shared.Infrastructure.Security;

/// <summary>
/// AES-256-GCM encryption for PII/SII at rest, plus HMAC-SHA256 blind indexes for searchable
/// fields. Keys come from <see cref="IEncryptionKeyProvider"/> (Vault). Per-purpose subkeys are
/// derived with HKDF-SHA256 so different purposes ("PII", "XenditToken", ...) stay isolated.
///
/// Ciphertext is a self-describing, versioned envelope so future key rotation or an algorithm
/// change is additive — no data migration required:
///   "bee.enc.v1:" + Base64( [alg:1][keyVer:1][nonce:12][tag:16][ciphertext:N] )
/// </summary>
public class DataProtectorService : IDataProtectorService
{
    public const string Prefix = "bee.enc.v1:";
    private const string PrefixFamily = "bee.enc."; // any version is treated as already-encrypted

    private const byte AlgAesGcm = 0x01;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int SubKeyBytes = 32;

    private readonly IEncryptionKeyProvider _keys;
    private readonly ILogger<DataProtectorService>? _logger;

    // Cache derived per-purpose subkeys (keyed by "version:purpose") to avoid re-running HKDF
    // on every field of every row.
    private readonly ConcurrentDictionary<string, byte[]> _subKeyCache = new();

    public DataProtectorService(IEncryptionKeyProvider keys, ILogger<DataProtectorService>? logger = null)
    {
        _keys = keys;
        _logger = logger;
    }

    public string Encrypt(string plainText, string purpose)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;
        if (plainText.StartsWith(PrefixFamily, StringComparison.Ordinal)) return plainText; // already encrypted

        var keyVer = _keys.CurrentMasterKeyVersion;
        var subKey = GetSubKey(keyVer, purpose);

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plain = Encoding.UTF8.GetBytes(plainText);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];

        using (var gcm = new AesGcm(subKey, TagBytes))
            gcm.Encrypt(nonce, plain, cipher, tag);

        var envelope = new byte[2 + NonceBytes + TagBytes + cipher.Length];
        envelope[0] = AlgAesGcm;
        envelope[1] = keyVer;
        Buffer.BlockCopy(nonce, 0, envelope, 2, NonceBytes);
        Buffer.BlockCopy(tag, 0, envelope, 2 + NonceBytes, TagBytes);
        Buffer.BlockCopy(cipher, 0, envelope, 2 + NonceBytes + TagBytes, cipher.Length);

        return Prefix + Convert.ToBase64String(envelope);
    }

    public string Decrypt(string cipherText, string purpose)
    {
        if (string.IsNullOrEmpty(cipherText)) return cipherText;
        if (!cipherText.StartsWith(PrefixFamily, StringComparison.Ordinal)) return cipherText; // not encrypted

        var base64 = cipherText.Substring(cipherText.IndexOf(':') + 1);
        byte[] envelope;
        try { envelope = Convert.FromBase64String(base64); }
        catch (FormatException ex) { throw new CryptographicException("Encrypted payload is not valid base64.", ex); }

        if (envelope.Length < 2 + NonceBytes + TagBytes)
            throw new CryptographicException("Encrypted payload is too short.");

        var alg = envelope[0];
        if (alg != AlgAesGcm)
            throw new CryptographicException($"Unsupported encryption algorithm id {alg}.");

        var keyVer = envelope[1];
        var subKey = GetSubKey(keyVer, purpose);

        var nonce = new byte[NonceBytes];
        var tag = new byte[TagBytes];
        var cipherLen = envelope.Length - 2 - NonceBytes - TagBytes;
        var cipher = new byte[cipherLen];
        Buffer.BlockCopy(envelope, 2, nonce, 0, NonceBytes);
        Buffer.BlockCopy(envelope, 2 + NonceBytes, tag, 0, TagBytes);
        Buffer.BlockCopy(envelope, 2 + NonceBytes + TagBytes, cipher, 0, cipherLen);

        var plain = new byte[cipherLen];
        try
        {
            using var gcm = new AesGcm(subKey, TagBytes);
            gcm.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException ex)
        {
            // Auth-tag mismatch: tampered data, wrong key, or wrong purpose. Fail loud — never
            // return ciphertext (the old Data Protection behaviour that surfaced as garbage PII).
            _logger?.LogError(ex, "Decryption failed for purpose {Purpose} (key v{KeyVer}). Payload tampered or key mismatch.", purpose, keyVer);
            throw;
        }

        return Encoding.UTF8.GetString(plain);
    }

    public string ComputeBlindIndex(string value, string field)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var normalized = Normalize(value, field);
        using var hmac = new HMACSHA256(_keys.BlindIndexKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(normalized + "|" + field));
        return Convert.ToBase64String(hash);
    }

    private byte[] GetSubKey(byte keyVer, string purpose)
    {
        return _subKeyCache.GetOrAdd($"{keyVer}:{purpose}", _ =>
        {
            var master = _keys.GetMasterKey(keyVer);
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm: master,
                outputLength: SubKeyBytes,
                salt: null,
                info: Encoding.UTF8.GetBytes(purpose));
        });
    }

    /// <summary>
    /// Normalizes a value before hashing so logically-equal inputs (formatting differences)
    /// produce the same blind index. Phone -> digits only; everything else -> trimmed lowercase.
    /// </summary>
    private static string Normalize(string value, string field)
    {
        if (field.Contains("phone", StringComparison.OrdinalIgnoreCase))
            return new string(value.Where(char.IsDigit).ToArray());

        return value.Trim().ToLowerInvariant();
    }
}
