using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Map.Migrations
{
    /// <inheritdoc />
    public partial class AddGeofenceTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DriverGeofenceStates",
                schema: "map",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeofenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsInside = table.Column<bool>(type: "boolean", nullable: false),
                    LastStateChange = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastChecked = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverGeofenceStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Geofences",
                schema: "map",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    PolygonCoordinates = table.Column<string>(type: "jsonb", nullable: true),
                    CenterLatitude = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    CenterLongitude = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    RadiusMeters = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Geofences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocationHistory",
                schema: "map",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: false),
                    Latitude = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Longitude = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Speed = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    Heading = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocationHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DriverGeofenceStates_DriverId",
                schema: "map",
                table: "DriverGeofenceStates",
                column: "DriverId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverGeofenceStates_DriverId_GeofenceId",
                schema: "map",
                table: "DriverGeofenceStates",
                columns: new[] { "DriverId", "GeofenceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriverGeofenceStates_GeofenceId",
                schema: "map",
                table: "DriverGeofenceStates",
                column: "GeofenceId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverGeofenceStates_LastChecked",
                schema: "map",
                table: "DriverGeofenceStates",
                column: "LastChecked");

            migrationBuilder.CreateIndex(
                name: "IX_Geofences_Category",
                schema: "map",
                table: "Geofences",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_Geofences_IsActive",
                schema: "map",
                table: "Geofences",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_LocationHistory_DriverId_Timestamp",
                schema: "map",
                table: "LocationHistory",
                columns: new[] { "DriverId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_LocationHistory_Timestamp",
                schema: "map",
                table: "LocationHistory",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DriverGeofenceStates",
                schema: "map");

            migrationBuilder.DropTable(
                name: "Geofences",
                schema: "map");

            migrationBuilder.DropTable(
                name: "LocationHistory",
                schema: "map");
        }
    }
}
