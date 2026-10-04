using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVehicleInfoToDriverApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VehicleColor",
                schema: "drivers",
                table: "DriverApplications",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehicleModel",
                schema: "drivers",
                table: "DriverApplications",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehiclePlate",
                schema: "drivers",
                table: "DriverApplications",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehicleType",
                schema: "drivers",
                table: "DriverApplications",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VehicleColor",
                schema: "drivers",
                table: "DriverApplications");

            migrationBuilder.DropColumn(
                name: "VehicleModel",
                schema: "drivers",
                table: "DriverApplications");

            migrationBuilder.DropColumn(
                name: "VehiclePlate",
                schema: "drivers",
                table: "DriverApplications");

            migrationBuilder.DropColumn(
                name: "VehicleType",
                schema: "drivers",
                table: "DriverApplications");
        }
    }
}
