using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Map.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "map",
                table: "LocationHistory");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "map",
                table: "Geofences");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "map",
                table: "DriverLocations");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "map",
                table: "DriverGeofenceStates");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "map",
                table: "LocationHistory",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "map",
                table: "Geofences",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "map",
                table: "DriverLocations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "map",
                table: "DriverGeofenceStates",
                type: "uuid",
                nullable: true);
        }
    }
}
