namespace BeeLogistics.Modules.Identity.Domain;

public class DriverVehicleAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DriverId { get; set; } = null!;
    public Guid VehicleId { get; set; }
    public bool IsPrimary { get; set; }
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;

    public ApplicationUser Driver { get; set; } = null!;
    public Vehicle Vehicle { get; set; } = null!;
}
