namespace BeeLogistics.Shared.Abstractions;

public static class UserRoles
{
    // System-level admin (back office only - not part of main app)
    public const string SuperAdmin = "SuperAdmin";
    
    // Main app roles (customer + independent-driver marketplace)
    public const string Customer = "Customer"; // Previously "Client"
    public const string Driver = "Driver";

    // Role groups for authorization
    public const string All = "SuperAdmin,Customer,Driver";
    
    // Legacy role constants (deprecated - kept for backward compatibility during migration)
    [Obsolete("Use Customer instead. This will be removed in a future version.")]
    public const string Client = "Customer";
    
    [Obsolete("This role is no longer supported. This will be removed in a future version.")]
    public const string BusinessClient = "BusinessClient";
    
    [Obsolete("This role is no longer supported. This will be removed in a future version.")]
    public const string Owner = "Owner";
    
    [Obsolete("This role is no longer supported. This will be removed in a future version.")]
    public const string Admin = "Admin";
    
    [Obsolete("This role is no longer supported. This will be removed in a future version.")]
    public const string Dispatcher = "Dispatcher";
}
