using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Domain;

/// <summary>
/// The fixed cashbond amount a driver of a given vehicle type must pay before they can be
/// offered bookings (issue #103). Versioned the same way <c>VehiclePricing</c> is, so a rate
/// change keeps an audit trail instead of silently overwriting the number a driver already paid.
/// </summary>
public class DriverCashBondConfig : Entity
{
    public string VehicleType { get; private set; } = null!;
    public decimal Amount { get; private set; }

    // Version tracking
    public int Version { get; private set; } = 1;
    public Guid? UpdatedByUserId { get; private set; }
    public string? UpdatedByUserName { get; private set; }

    // Navigation property for version history
    private readonly List<DriverCashBondConfigVersion> _versions = new();
    public IReadOnlyCollection<DriverCashBondConfigVersion> Versions => _versions.AsReadOnly();

    private DriverCashBondConfig() { } // For EF Core

    public DriverCashBondConfig(
        string vehicleType,
        decimal amount,
        Guid? createdByUserId = null,
        string? createdByUserName = null)
    {
        if (string.IsNullOrWhiteSpace(vehicleType))
            throw new ArgumentException("Vehicle type is required", nameof(vehicleType));

        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));

        Id = Guid.NewGuid();
        VehicleType = vehicleType;
        Amount = amount;
        Version = 1;
        UpdatedByUserId = createdByUserId;
        UpdatedByUserName = createdByUserName;

        // Initial version will be created by repository/service layer,
        // same as VehiclePricing, to ensure proper EF Core tracking.
    }

    /// <summary>
    /// Updates the amount and bumps the version for audit tracking. The version snapshot itself
    /// is created by the command handler, not here — same split of responsibility as
    /// <c>VehiclePricing.UpdatePricing</c>.
    /// </summary>
    public void UpdateAmount(decimal amount, Guid? updatedByUserId = null, string? updatedByUserName = null)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));

        Amount = amount;
        Version++;
        UpdatedByUserId = updatedByUserId;
        UpdatedByUserName = updatedByUserName;
        UpdatedAt = DateTime.UtcNow;
    }
}
