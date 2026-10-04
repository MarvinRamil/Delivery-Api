using BeeLogistics.Modules.Map.Domain;

namespace BeeLogistics.Modules.Map.Application.Interfaces;

public interface IGeofenceRepository
{
    Task<List<Geofence>> GetActiveGeofencesAsync(CancellationToken ct = default);
    Task<Geofence?> GetByIdAsync(Guid id, CancellationToken ct = default);
    void Add(Geofence geofence);
    void Update(Geofence geofence);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IDriverGeofenceStateRepository
{
    Task<DriverGeofenceState?> GetByDriverAndGeofenceAsync(Guid driverId, Guid geofenceId, CancellationToken ct = default);
    Task<List<DriverGeofenceState>> GetByDriverIdAsync(Guid driverId, CancellationToken ct = default);
    void Add(DriverGeofenceState state);
    void Update(DriverGeofenceState state);
    Task SaveChangesAsync(CancellationToken ct = default);
}

