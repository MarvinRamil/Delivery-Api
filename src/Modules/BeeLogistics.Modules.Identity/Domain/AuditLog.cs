namespace BeeLogistics.Modules.Identity.Domain;

/// <summary>
/// Represents an audit log entry for tracking user actions
/// </summary>
public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Action { get; set; } = null!;
    public string Category { get; set; } = null!;
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }
    public string? UserRole { get; set; }
    public string? EntityId { get; set; }
    public string? EntityType { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? RequestId { get; set; }
    public bool IsSuccess { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    // Soft delete
    public bool IsArchived { get; set; } = false;
    public DateTime? ArchivedAt { get; set; }
}

public static class AuditActions
{
    public const string Login = "Login";
    public const string LoginFailed = "LoginFailed";
    public const string Logout = "Logout";
    public const string LogoutAll = "LogoutAll";
    public const string PasswordChanged = "PasswordChanged";
    public const string PasswordReset = "PasswordReset";
    public const string PasswordResetRequested = "PasswordResetRequested";
    public const string ProfileUpdated = "ProfileUpdated";
    public const string UserCreated = "UserCreated";
    public const string TokenRevoked = "TokenRevoked";
    public const string EmailVerified = "EmailVerified";
}

public static class AuditCategories
{
    public const string Auth = "Auth";
    public const string User = "User";
}

