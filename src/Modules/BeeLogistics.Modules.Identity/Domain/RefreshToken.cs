using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Identity.Domain;

public class RefreshToken : Entity
{
    public string UserId { get; private set; } = string.Empty;
    public Guid? CustomerId { get; private set; }
    public string Token { get; private set; } = string.Empty;
    public DateTime ExpiresAt { get; private set; }
    public bool IsRevoked { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public string? ReplacedByToken { get; private set; }
    public string? DeviceId { get; private set; }
    public string? DeviceFingerprint { get; private set; }

    private RefreshToken() { } // For EF Core

    public RefreshToken(string userId, string token, DateTime expiresAt, Guid? customerId = null, string? deviceId = null, string? deviceFingerprint = null)
    {
        UserId = userId;
        Token = token;
        ExpiresAt = expiresAt;
        CustomerId = customerId;
        DeviceId = deviceId;
        DeviceFingerprint = deviceFingerprint;
        IsRevoked = false;
    }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    public bool IsActive => !IsRevoked && !IsExpired;

    public void Revoke(string? replacedByToken = null)
    {
        IsRevoked = true;
        RevokedAt = DateTime.UtcNow;
        ReplacedByToken = replacedByToken;
    }
}
