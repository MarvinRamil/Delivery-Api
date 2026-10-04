using BeeLogistics.Modules.Map.Domain;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace BeeLogistics.Modules.Map.Infrastructure;

public class MapDbContext : DbContext
{
    public MapDbContext(DbContextOptions<MapDbContext> options) : base(options)
    {
    }

    public DbSet<DriverLocation> DriverLocations { get; set; }
    public DbSet<DriverLocationHistory> LocationHistory { get; set; }
    public DbSet<Geofence> Geofences { get; set; }
    public DbSet<DriverGeofenceState> DriverGeofenceStates { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("map");

        modelBuilder.Entity<DriverLocation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Latitude).HasPrecision(18, 8);
            entity.Property(e => e.Longitude).HasPrecision(18, 8);
            entity.Property(e => e.Speed).HasPrecision(10, 2);
            entity.Property(e => e.Heading).HasPrecision(10, 2);
            entity.Property(e => e.DeviceId).HasMaxLength(500);
            
            // PostGIS geometry column for efficient spatial queries
            // This will be populated from Latitude/Longitude in the migration
            entity.Property(e => e.Location)
                .HasColumnType("geometry(Point, 4326)") // WGS84 coordinate system
                .HasColumnName("Location");

            entity.HasIndex(e => e.DriverId);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => new { e.DriverId, e.Timestamp });
            // Spatial index for PostGIS queries (GIST index)
            entity.HasIndex(e => e.Location)
                .HasMethod("GIST")
                .HasDatabaseName("IX_DriverLocations_Location_GIST");
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        // Location History (Time-series data)
        modelBuilder.Entity<DriverLocationHistory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Latitude).HasPrecision(18, 8);
            entity.Property(e => e.Longitude).HasPrecision(18, 8);
            entity.Property(e => e.Speed).HasPrecision(10, 2);
            entity.Property(e => e.Heading).HasPrecision(10, 2);
            entity.Property(e => e.DeviceId).HasMaxLength(500);
            entity.Property(e => e.Accuracy).HasPrecision(10, 2);

            // Stored as int rather than a string so the column stays cheap on what is by far the
            // highest-volume table in the schema.
            entity.Property(e => e.Source).HasConversion<int>();

            // PostGIS geometry column for efficient spatial queries
            entity.Property(e => e.Location)
                .HasColumnType("geometry(Point, 4326)")
                .HasColumnName("Location");

            // Optimized for time-range queries
            entity.HasIndex(e => new { e.DriverId, e.Timestamp })
                .HasDatabaseName("IX_LocationHistory_DriverId_Timestamp");
            
            // For analytics queries
            entity.HasIndex(e => e.Timestamp)
                .HasDatabaseName("IX_LocationHistory_Timestamp");
            
            // Spatial index for PostGIS queries
            entity.HasIndex(e => e.Location)
                .HasMethod("GIST")
                .HasDatabaseName("IX_LocationHistory_Location_GIST");

            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        // Geofences
        modelBuilder.Entity<Geofence>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Type).IsRequired();
            entity.Property(e => e.PolygonCoordinates).HasColumnType("jsonb");
            entity.Property(e => e.CenterLatitude).HasPrecision(18, 8);
            entity.Property(e => e.CenterLongitude).HasPrecision(18, 8);
            entity.Property(e => e.RadiusMeters).HasPrecision(10, 2);
            entity.Property(e => e.Metadata).HasColumnType("jsonb");
            entity.Property(e => e.Category).HasMaxLength(100);

            entity.HasIndex(e => e.IsActive);
            entity.HasIndex(e => e.Category);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        // Driver Geofence States
        modelBuilder.Entity<DriverGeofenceState>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.DriverId, e.GeofenceId }).IsUnique();
            entity.HasIndex(e => e.DriverId);
            entity.HasIndex(e => e.GeofenceId);
            entity.HasIndex(e => e.LastChecked);

            entity.HasQueryFilter(e => !e.IsDeleted);
        });
    }
}
