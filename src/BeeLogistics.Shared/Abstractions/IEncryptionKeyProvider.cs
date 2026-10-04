namespace BeeLogistics.Shared.Abstractions;

/// <summary>
/// Supplies the symmetric key material used for PII/SII encryption and blind indexing.
/// Implementations load keys once (e.g. from HashiCorp Vault) and cache them for the
/// process lifetime. Key material never leaves the process after load.
/// </summary>
public interface IEncryptionKeyProvider
{
    /// <summary>
    /// The key version used when encrypting new values. Embedded in the ciphertext envelope
    /// so older values keep decrypting after a future rotation.
    /// </summary>
    byte CurrentMasterKeyVersion { get; }

    /// <summary>
    /// Returns the 32-byte AES-256 master key for the given version.
    /// Throws if the version is unknown (wrong/rotated-away key -> fail loud, never silent).
    /// </summary>
    byte[] GetMasterKey(byte version);

    /// <summary>
    /// The 32-byte HMAC key used for deterministic blind indexes. Single, non-versioned:
    /// rotating it would invalidate existing lookup hashes.
    /// </summary>
    byte[] BlindIndexKey { get; }
}
