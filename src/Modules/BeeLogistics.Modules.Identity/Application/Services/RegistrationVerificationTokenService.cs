using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;

namespace BeeLogistics.Modules.Identity.Application.Services;

public interface IRegistrationVerificationTokenService
{
    /// <summary>
    /// Create a stateless token proving OTP was verified for this email. Returns an empty string
    /// when no signing secret is configured.
    /// </summary>
    string Create(string normalizedEmail);

    /// <summary>
    /// True when <paramref name="token"/> is a valid, unexpired proof that OTP was verified for
    /// <paramref name="normalizedEmail"/>.
    /// </summary>
    bool Validate(string? token, string normalizedEmail);
}

/// <summary>
/// Stateless "OTP was verified" proof, used when the distributed cache is not shared across API
/// instances. Format is <c>base64url(payload).base64url(HMACSHA256(secret, payloadB64))</c> where
/// payload is <c>{email}|{unixExpiry}</c>.
/// </summary>
public class RegistrationVerificationTokenService : IRegistrationVerificationTokenService
{
    private const int RegistrationTokenExpiryMinutes = 20;

    private readonly IConfiguration _configuration;

    public RegistrationVerificationTokenService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string Create(string normalizedEmail)
    {
        var secret = _configuration["JwtSettings:Secret"];
        if (string.IsNullOrEmpty(secret)) return string.Empty;
        var expiry = DateTimeOffset.UtcNow.AddMinutes(RegistrationTokenExpiryMinutes).ToUnixTimeSeconds();
        var payload = $"{normalizedEmail}|{expiry}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var payloadB64 = WebEncoders.Base64UrlEncode(payloadBytes);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadB64));
        var sigB64 = WebEncoders.Base64UrlEncode(sig);
        return $"{payloadB64}.{sigB64}";
    }

    public bool Validate(string? token, string normalizedEmail)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var secret = _configuration["JwtSettings:Secret"];
        if (string.IsNullOrEmpty(secret)) return false;
        var parts = token.Split('.');
        if (parts.Length != 2) return false;
        var payloadB64 = parts[0];
        var sigB64 = parts[1];
        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var expectedSig = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadB64));
            var expectedSigB64 = WebEncoders.Base64UrlEncode(expectedSig);
            if (sigB64 != expectedSigB64) return false;
            var payloadBytes = WebEncoders.Base64UrlDecode(payloadB64);
            var payload = Encoding.UTF8.GetString(payloadBytes);
            var lastPipe = payload.LastIndexOf('|');
            if (lastPipe <= 0) return false;
            var email = payload[..lastPipe];
            var expStr = payload[(lastPipe + 1)..];
            if (email != normalizedEmail) return false;
            if (!long.TryParse(expStr, out var exp)) return false;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
