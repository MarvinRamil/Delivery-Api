using BeeLogistics.Modules.Map.Application.Interfaces;
using BeeLogistics.Modules.Map.Domain;
using BeeLogistics.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeeLogistics.Modules.Map.Infrastructure.Repositories;

public class GeofenceRepository : Repository<Geofence, MapDbContext>, IGeofenceRepository
{
    public GeofenceRepository(MapDbContext context) : base(context) { }

    public async Task<List<Geofence>> GetActiveGeofencesAsync(CancellationToken ct = default)
    {
        try
        {
            return await DbSet
                .Where(g => g.IsActive && !g.IsDeleted)
                .ToListAsync(ct);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01") // Table does not exist
        {
            // Table doesn't exist yet (migrations not run or schema not created)
            // Return empty list to allow the system to continue functioning
            return new List<Geofence>();
        }
    }

    public new async Task<Geofence?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await DbSet
            .FirstOrDefaultAsync(g => g.Id == id && !g.IsDeleted, ct);
    }
}

public class DriverGeofenceStateRepository : Repository<DriverGeofenceState, MapDbContext>, IDriverGeofenceStateRepository
{
    public DriverGeofenceStateRepository(MapDbContext context) : base(context) { }

    public async Task<DriverGeofenceState?> GetByDriverAndGeofenceAsync(Guid driverId, Guid geofenceId, CancellationToken ct = default)
    {
        try
        {
            return await DbSet
                .FirstOrDefaultAsync(s => s.DriverId == driverId && s.GeofenceId == geofenceId && !s.IsDeleted, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // Table does not exist
        {
            // Table doesn't exist yet - return null
            return null;
        }
    }

    public async Task<List<DriverGeofenceState>> GetByDriverIdAsync(Guid driverId, CancellationToken ct = default)
    {
        try
        {
            return await DbSet
                .Where(s => s.DriverId == driverId && !s.IsDeleted)
                .ToListAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // Table does not exist
        {
            // Table doesn't exist yet - return empty list
            return new List<DriverGeofenceState>();
        }
    }
}

