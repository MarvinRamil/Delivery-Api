# PostGIS Migration Plan

## Overview

This document outlines the migration from application-level Haversine distance calculations to PostGIS for efficient geospatial queries in the BeeLogistics Map module.

## Benefits of PostGIS

1. **Performance**: Spatial indexes (GIST) provide 10-100x faster queries than application-level calculations
2. **Accuracy**: Native PostGIS functions handle Earth's curvature and coordinate systems correctly
3. **Scalability**: Database-level spatial operations scale better than in-memory calculations
4. **Features**: Access to advanced spatial functions (buffers, intersections, etc.)

## Migration Steps

### 1. ✅ Package Installation
- Added `Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite` package to Map module
- Enables PostGIS geometry types in EF Core

### 2. ✅ Database Schema Updates
- **Migration**: `20250127000001_AddPostGISSupport`
- Enables PostGIS extension: `CREATE EXTENSION IF NOT EXISTS postgis;`
- Adds `Location` geometry columns (Point, SRID 4326) to:
  - `DriverLocations` table
  - `LocationHistory` table
- Creates GIST spatial indexes for fast queries
- Creates database triggers to auto-populate `Location` from `Latitude`/`Longitude`

### 3. ✅ Domain Model Updates
- Added `Location` property (Point?) to:
  - `DriverLocation` entity
  - `DriverLocationHistory` entity
- Kept `Latitude`/`Longitude` for backward compatibility

### 4. ✅ Repository Updates
- Updated `LocationRepository.GetLocationsWithinRadiusAsync()` to use PostGIS `ST_DWithin`
- Replaced Haversine formula with database-level spatial query
- Uses spatial index for optimal performance

## Database Triggers

The migration creates triggers that automatically update the `Location` geometry column whenever `Latitude` or `Longitude` changes:

```sql
CREATE TRIGGER update_driver_location_geometry
BEFORE INSERT OR UPDATE ON map."DriverLocations"
FOR EACH ROW
EXECUTE FUNCTION map.update_location_from_coordinates();
```

This ensures data consistency without requiring application code changes.

## Query Performance Comparison

### Before (Haversine in C#)
- Loads all locations into memory
- Filters by bounding box
- Calculates distance for each location
- **Performance**: O(n) where n = all locations in bounding box
- **Example**: 10,000 locations = ~100ms

### After (PostGIS ST_DWithin)
- Uses spatial index (GIST) to find locations
- Database-level filtering
- **Performance**: O(log n) with spatial index
- **Example**: 10,000 locations = ~5-10ms

## Usage Examples

### Finding Locations Within Radius

```csharp
// Old way (still works, but slower)
var locations = await repository.GetLocationsWithinRadiusAsync(
    centerLat: 14.5995m,
    centerLng: 120.9842m,
    radiusKm: 5.0m
);

// PostGIS query is now used internally
// ST_DWithin with spatial index provides optimal performance
```

### Future PostGIS Features

With PostGIS enabled, you can now use advanced spatial operations:

```sql
-- Find locations within a polygon
SELECT * FROM map."DriverLocations"
WHERE ST_Within("Location", @polygon);

-- Calculate distance between points
SELECT ST_Distance(l1."Location", l2."Location")
FROM map."DriverLocations" l1, map."DriverLocations" l2;

-- Buffer operations
SELECT ST_Buffer("Location", 1000) -- 1km buffer
FROM map."DriverLocations";
```

## Rollback Plan

If you need to rollback:

1. Run migration down: `dotnet ef database update <previous-migration> --context MapDbContext`
2. This will:
   - Drop spatial indexes
   - Drop Location columns
   - Drop triggers and functions
   - Keep PostGIS extension (doesn't affect other features)

## Testing

### Verify PostGIS Installation
```sql
SELECT PostGIS_version();
-- Should return version number (e.g., "3.3 USE_GEOS=1 USE_PROJ=1")
```

### Verify Location Column Population
```sql
SELECT COUNT(*) FROM map."DriverLocations" WHERE "Location" IS NOT NULL;
-- Should match count of records with Latitude/Longitude
```

### Test Spatial Query Performance
```sql
EXPLAIN ANALYZE
SELECT * FROM map."DriverLocations"
WHERE ST_DWithin(
    "Location"::geography,
    ST_SetSRID(ST_MakePoint(120.9842, 14.5995), 4326)::geography,
    5000
);
-- Should show "Index Scan using IX_DriverLocations_Location_GIST"
```

## Next Steps

1. ✅ Run migration: `dotnet ef database update --context MapDbContext`
2. ✅ Verify PostGIS extension is enabled
3. ✅ Test `GetLocationsWithinRadiusAsync` performance
4. ⏳ Consider migrating geofence checks to use PostGIS
5. ⏳ Add PostGIS support for route optimization queries

## Notes

- **Backward Compatibility**: Existing code using `Latitude`/`Longitude` continues to work
- **Data Consistency**: Triggers ensure `Location` is always in sync with `Latitude`/`Longitude`
- **Performance**: Spatial indexes are automatically maintained by PostgreSQL
- **Coordinate System**: Using SRID 4326 (WGS84) - standard for GPS coordinates
