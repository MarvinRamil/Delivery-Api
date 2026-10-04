namespace BeeLogistics.Modules.Fraud.Application;

public class FraudOptions
{
    public const string SectionName = "Fraud";

    public int DriverDeliveriesThreshold { get; set; } = 10;
    public int DriverDeliveriesWindowMinutes { get; set; } = 5;

    public decimal GpsJumpKmThreshold { get; set; } = 50;

    public int CustomerOrdersThreshold { get; set; } = 20;
    public int CustomerOrdersWindowMinutes { get; set; } = 2;

    public int DeviceUsersThreshold { get; set; } = 5;
}
