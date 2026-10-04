using System.Security.Cryptography;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Infrastructure.Security;
using Xunit;

namespace BeeLogistics.Tests.Security;

public class DataProtectorServiceTests
{
    private static DataProtectorService NewService() => new(new FakeKeyProvider());

    private sealed class FakeKeyProvider : IEncryptionKeyProvider
    {
        private readonly byte[] _master = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        private readonly byte[] _blind = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
        public byte CurrentMasterKeyVersion => 1;
        public byte[] GetMasterKey(byte version) => version == 1 ? _master
            : throw new InvalidOperationException($"no key v{version}");
        public byte[] BlindIndexKey => _blind;
    }

    [Fact]
    public void Encrypt_then_Decrypt_round_trips()
    {
        var svc = NewService();
        const string plain = "+639171234567";

        var cipher = svc.Encrypt(plain, "PII");

        Assert.StartsWith(DataProtectorService.Prefix, cipher);
        Assert.NotEqual(plain, cipher);
        Assert.Equal(plain, svc.Decrypt(cipher, "PII"));
    }

    [Fact]
    public void Encrypt_is_non_deterministic_but_both_decrypt()
    {
        var svc = NewService();
        var a = svc.Encrypt("secret", "PII");
        var b = svc.Encrypt("secret", "PII");

        Assert.NotEqual(a, b); // random nonce per call
        Assert.Equal("secret", svc.Decrypt(a, "PII"));
        Assert.Equal("secret", svc.Decrypt(b, "PII"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_is_passed_through(string? value)
    {
        var svc = NewService();
        Assert.Equal(value, svc.Encrypt(value!, "PII"));
        Assert.Equal(value, svc.Decrypt(value!, "PII"));
    }

    [Fact]
    public void Encrypt_is_idempotent_on_already_encrypted_value()
    {
        var svc = NewService();
        var once = svc.Encrypt("hello", "PII");
        var twice = svc.Encrypt(once, "PII");

        Assert.Equal(once, twice); // not double-wrapped
        Assert.Equal("hello", svc.Decrypt(twice, "PII"));
    }

    [Fact]
    public void Decrypt_of_plaintext_is_passed_through()
    {
        var svc = NewService();
        Assert.Equal("not-encrypted", svc.Decrypt("not-encrypted", "PII"));
    }

    [Fact]
    public void Decrypt_with_wrong_purpose_throws()
    {
        var svc = NewService();
        var cipher = svc.Encrypt("secret", "PII");

        // Different purpose -> different HKDF subkey -> GCM auth-tag mismatch.
        Assert.ThrowsAny<CryptographicException>(() => svc.Decrypt(cipher, "OtherPurpose"));
    }

    [Fact]
    public void Tampered_payload_throws()
    {
        var svc = NewService();
        var cipher = svc.Encrypt("secret", "PII");

        var base64 = cipher.Substring(cipher.IndexOf(':') + 1);
        var bytes = Convert.FromBase64String(base64);
        bytes[^1] ^= 0xFF; // flip a ciphertext byte
        var tampered = DataProtectorService.Prefix + Convert.ToBase64String(bytes);

        Assert.ThrowsAny<CryptographicException>(() => svc.Decrypt(tampered, "PII"));
    }

    [Fact]
    public void BlindIndex_is_deterministic()
    {
        var svc = NewService();
        Assert.Equal(svc.ComputeBlindIndex("a@b.com", "email"), svc.ComputeBlindIndex("a@b.com", "email"));
    }

    [Fact]
    public void BlindIndex_normalizes_email_case_and_whitespace()
    {
        var svc = NewService();
        var canonical = svc.ComputeBlindIndex("user@example.com", "email");
        Assert.Equal(canonical, svc.ComputeBlindIndex("  USER@Example.COM  ", "email"));
    }

    [Fact]
    public void BlindIndex_normalizes_phone_formatting()
    {
        var svc = NewService();
        var canonical = svc.ComputeBlindIndex("09171234567", "phone");
        Assert.Equal(canonical, svc.ComputeBlindIndex("0917 123 4567", "phone"));
        Assert.Equal(canonical, svc.ComputeBlindIndex("0917-123-4567", "phone"));
    }

    [Fact]
    public void BlindIndex_differs_by_field_scope()
    {
        var svc = NewService();
        Assert.NotEqual(svc.ComputeBlindIndex("same", "email"), svc.ComputeBlindIndex("same", "other"));
    }
}
