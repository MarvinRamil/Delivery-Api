namespace BeeLogistics.Modules.Identity.Domain;

public class Vehicle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PlateNumber { get; set; } = null!;
    public string? Model { get; set; }
    public string? Color { get; set; }
    public string? Type { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<DriverVehicleAssignment> DriverVehicleAssignments { get; set; } = new List<DriverVehicleAssignment>();
}
