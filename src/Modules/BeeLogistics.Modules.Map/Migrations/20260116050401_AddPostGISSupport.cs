using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace BeeLogistics.Modules.Map.Migrations
{
    /// <inheritdoc />
    public partial class AddPostGISSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.AddColumn<Point>(
                name: "Location",
                schema: "map",
                table: "LocationHistory",
                type: "geometry(Point, 4326)",
                nullable: true);

            migrationBuilder.AddColumn<Point>(
                name: "Location",
                schema: "map",
                table: "DriverLocations",
                type: "geometry(Point, 4326)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LocationHistory_Location_GIST",
                schema: "map",
                table: "LocationHistory",
                column: "Location")
                .Annotation("Npgsql:IndexMethod", "GIST");

            migrationBuilder.CreateIndex(
                name: "IX_DriverLocations_Location_GIST",
                schema: "map",
                table: "DriverLocations",
                column: "Location")
                .Annotation("Npgsql:IndexMethod", "GIST");

            // Enable PostGIS extension (if not already enabled)
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS postgis;", suppressTransaction: false);

            // Populate Location columns from existing Latitude/Longitude data
            migrationBuilder.Sql(@"
                UPDATE map.""DriverLocations""
                SET ""Location"" = ST_SetSRID(ST_MakePoint(""Longitude""::double precision, ""Latitude""::double precision), 4326)
                WHERE ""Location"" IS NULL AND ""Latitude"" IS NOT NULL AND ""Longitude"" IS NOT NULL;
            ");

            migrationBuilder.Sql(@"
                UPDATE map.""LocationHistory""
                SET ""Location"" = ST_SetSRID(ST_MakePoint(""Longitude""::double precision, ""Latitude""::double precision), 4326)
                WHERE ""Location"" IS NULL AND ""Latitude"" IS NOT NULL AND ""Longitude"" IS NOT NULL;
            ");

            // Create trigger function to auto-update Location from Latitude/Longitude
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION map.update_location_from_coordinates()
                RETURNS TRIGGER AS $$
                BEGIN
                    IF NEW.""Latitude"" IS NOT NULL AND NEW.""Longitude"" IS NOT NULL THEN
                        NEW.""Location"" := ST_SetSRID(ST_MakePoint(NEW.""Longitude""::double precision, NEW.""Latitude""::double precision), 4326);
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
            ");

            // Create triggers
            migrationBuilder.Sql(@"
                CREATE TRIGGER update_driver_location_geometry
                BEFORE INSERT OR UPDATE ON map.""DriverLocations""
                FOR EACH ROW
                EXECUTE FUNCTION map.update_location_from_coordinates();
            ");

            migrationBuilder.Sql(@"
                CREATE TRIGGER update_location_history_geometry
                BEFORE INSERT OR UPDATE ON map.""LocationHistory""
                FOR EACH ROW
                EXECUTE FUNCTION map.update_location_from_coordinates();
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LocationHistory_Location_GIST",
                schema: "map",
                table: "LocationHistory");

            migrationBuilder.DropIndex(
                name: "IX_DriverLocations_Location_GIST",
                schema: "map",
                table: "DriverLocations");

            migrationBuilder.DropColumn(
                name: "Location",
                schema: "map",
                table: "LocationHistory");

            migrationBuilder.DropColumn(
                name: "Location",
                schema: "map",
                table: "DriverLocations");

            // Drop triggers and function
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS update_location_history_geometry ON map.\"LocationHistory\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS update_driver_location_geometry ON map.\"DriverLocations\";");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS map.update_location_from_coordinates();");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,");
        }
    }
}
