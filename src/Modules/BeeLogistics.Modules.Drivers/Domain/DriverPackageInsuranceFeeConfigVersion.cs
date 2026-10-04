using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Domain;

/// <summary>
/// Historical snapshot of a <see cref="DriverPackageInsuranceFeeConfig"/> rate, for audit and
/// tracking. A new version is created whenever the amount is updated.
/// </summary>
public class DriverPackageInsuranceFeeConfigVersion : Entity
{
    public Guid DriverPackageInsuranceFeeConfigId { get; private set; }
    public int Version { get; private set; }
    public decimal Amount { get; private set; }

    // Who made the change
    public Guid? ChangedByUserId { get; private set; }
    public string? ChangedByUserName { get; private set; }

    // Navigation property
    public DriverPackageInsuranceFeeConfig DriverPackageInsuranceFeeConfig { get; private set; } = null!;

    private DriverPackageInsuranceFeeConfigVersion() { } // For EF Core

    public DriverPackageInsuranceFeeConfigVersion(
        Guid driverPackageInsuranceFeeConfigId,
        int version,
        decimal amount,
        Guid? changedByUserId = null,
        string? changedByUserName = null)
    {
        Id = Guid.NewGuid();
        DriverPackageInsuranceFeeConfigId = driverPackageInsuranceFeeConfigId;
        Version = version;
        Amount = amount;
        ChangedByUserId = changedByUserId;
        ChangedByUserName = changedByUserName;
    }
}
