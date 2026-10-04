using BeeLogistics.Modules.Drivers.Domain;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// The per-vehicle-type cashbond rate (issue #103), versioned the same way VehiclePricing is so a
/// rate change keeps an audit trail rather than silently overwriting what a driver already paid.
/// </summary>
public class DriverCashBondConfigTests
{
    [Fact]
    public void A_new_config_starts_at_version_one()
    {
        var config = new DriverCashBondConfig("Motorcycle", 1000m);

        Assert.Equal(1, config.Version);
        Assert.Equal(1000m, config.Amount);
    }

    [Fact]
    public void Updating_the_amount_bumps_the_version()
    {
        var config = new DriverCashBondConfig("Motorcycle", 1000m);

        config.UpdateAmount(1500m);

        Assert.Equal(2, config.Version);
        Assert.Equal(1500m, config.Amount);
    }

    [Fact]
    public void Amount_must_be_positive()
    {
        Assert.Throws<ArgumentException>(() => new DriverCashBondConfig("Motorcycle", 0m));
        Assert.Throws<ArgumentException>(() => new DriverCashBondConfig("Motorcycle", -1m));

        var config = new DriverCashBondConfig("Motorcycle", 1000m);
        Assert.Throws<ArgumentException>(() => config.UpdateAmount(0m));
    }
}
