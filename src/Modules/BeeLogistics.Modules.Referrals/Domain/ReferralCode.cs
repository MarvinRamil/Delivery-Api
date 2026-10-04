using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Referrals.Domain;

public class ReferralCode : Entity
{
    public Guid UserId { get; private set; } // Driver or Customer
    public string Code { get; private set; } = string.Empty; // Unique referral code
    public ReferralUserType UserType { get; private set; } // Driver or Customer
    public bool IsActive { get; private set; }
    public string ReferralLink { get; private set; } = string.Empty; // Full URL: https://bee.app/ref/{code}
    
    // Navigation
    private readonly List<Referral> _referrals = new();
    public IReadOnlyCollection<Referral> Referrals => _referrals.AsReadOnly();

    private ReferralCode() { } // For EF Core

    public ReferralCode(Guid userId, string code, ReferralUserType userType, string baseUrl)
    {
        UserId = userId;
        Code = code;
        UserType = userType;
        IsActive = true;
        ReferralLink = $"{baseUrl.TrimEnd('/')}/ref/{code}";
        CreatedAt = DateTime.UtcNow;
    }

    public static ReferralCode Create(Guid userId, ReferralUserType userType, string baseUrl)
    {
        // Generate unique code: First 8 chars of userId + random 4 chars
        var userIdPart = userId.ToString("N")[..8].ToUpperInvariant();
        var randomPart = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var code = $"{userIdPart}{randomPart}";
        
        return new ReferralCode(userId, code, userType, baseUrl);
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Activate()
    {
        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }
}

public enum ReferralUserType
{
    Driver = 0,
    Customer = 1
}

