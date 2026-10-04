using BeeLogistics.Modules.Drivers.Domain;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// The per-vehicle-type annual package-insurance premium (issue #104), versioned the same way
/// DriverCashBondConfig is so a rate change keeps an audit trail rather than silently overwriting
/// what a driver already paid.
/// </summary>
public class DriverPackageInsuranceFeeConfigTests
{
    [Fact]
    public void A_new_config_starts_at_version_one()
    {
        var config = new DriverPackageInsuranceFeeConfig("Motorcycle", 500m);

        Assert.Equal(1, config.Version);
        Assert.Equal(500m, config.Amount);
    }

    [Fact]
    public void Updating_the_amount_bumps_the_version()
    {
        var config = new DriverPackageInsuranceFeeConfig("Motorcycle", 500m);

        config.UpdateAmount(750m);

        Assert.Equal(2, config.Version);
        Assert.Equal(750m, config.Amount);
    }

    [Fact]
    public void Amount_must_be_positive()
    {
        Assert.Throws<ArgumentException>(() => new DriverPackageInsuranceFeeConfig("Motorcycle", 0m));
        Assert.Throws<ArgumentException>(() => new DriverPackageInsuranceFeeConfig("Motorcycle", -1m));

        var config = new DriverPackageInsuranceFeeConfig("Motorcycle", 500m);
        Assert.Throws<ArgumentException>(() => config.UpdateAmount(0m));
    }

    [Fact]
    public void Vehicle_type_is_required()
    {
        Assert.Throws<ArgumentException>(() => new DriverPackageInsuranceFeeConfig("", 500m));
        Assert.Throws<ArgumentException>(() => new DriverPackageInsuranceFeeConfig("   ", 500m));
    }
}
