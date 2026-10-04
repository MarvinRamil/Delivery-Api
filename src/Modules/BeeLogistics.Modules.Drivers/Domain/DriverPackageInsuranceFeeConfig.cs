using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Domain;

/// <summary>
/// The annual package-insurance premium a driver of a given vehicle type must pay to keep
/// coverage active (issue #104). Coverage for the packages a driver carries, not the vehicle or
/// the driver's own liability — separate from <see cref="DriverApplication.InsurancePath"/>,
/// which is the driver's vehicle insurance document.
///
/// Versioned the same way <see cref="DriverCashBondConfig"/> is, kept as its own table rather
/// than columns added onto it: a shared version counter would bump for changes to either fee,
/// blurring both audit trails.
/// </summary>
public class DriverPackageInsuranceFeeConfig : Entity
{
    public string VehicleType { get; private set; } = null!;
    public decimal Amount { get; private set; }

    // Version tracking
    public int Version { get; private set; } = 1;
    public Guid? UpdatedByUserId { get; private set; }
    public string? UpdatedByUserName { get; private set; }

    // Navigation property for version history
    private readonly List<DriverPackageInsuranceFeeConfigVersion> _versions = new();
    public IReadOnlyCollection<DriverPackageInsuranceFeeConfigVersion> Versions => _versions.AsReadOnly();

    private DriverPackageInsuranceFeeConfig() { } // For EF Core

    public DriverPackageInsuranceFeeConfig(
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
        // same as DriverCashBondConfig, to ensure proper EF Core tracking.
    }

    /// <summary>
    /// Updates the amount and bumps the version for audit tracking. The version snapshot itself
    /// is created by the command handler, not here — same split of responsibility as
    /// <c>DriverCashBondConfig.UpdateAmount</c>.
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
