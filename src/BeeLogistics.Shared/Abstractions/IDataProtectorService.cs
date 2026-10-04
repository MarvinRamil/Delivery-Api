namespace BeeLogistics.Shared.Abstractions;

/// <summary>
/// Encrypts/decrypts PII/SII at rest with AES-256-GCM, and computes deterministic blind
/// indexes for fields that must remain searchable while encrypted.
/// </summary>
public interface IDataProtectorService
{
    /// <summary>
    /// Encrypts <paramref name="plainText"/> for the given logical <paramref name="purpose"/>
    /// (used to derive an isolated subkey). Returns a versioned "bee.enc.v1:" payload.
    /// Null/empty and already-encrypted input is returned unchanged.
    /// </summary>
    string Encrypt(string plainText, string purpose);

    /// <summary>
    /// Decrypts a "bee.enc." payload for the given purpose. Null/empty and non-encrypted input
    /// is returned unchanged. Throws on tampered payloads or a missing/incorrect key.
    /// </summary>
    string Decrypt(string cipherText, string purpose);

    /// <summary>
    /// Computes a deterministic, queryable HMAC-SHA256 blind index over a normalized value,
    /// scoped to <paramref name="field"/>. Equal logical values produce equal indexes, enabling
    /// equality lookups against an encrypted column without exposing the plaintext.
    /// </summary>
    string ComputeBlindIndex(string value, string field);
}
