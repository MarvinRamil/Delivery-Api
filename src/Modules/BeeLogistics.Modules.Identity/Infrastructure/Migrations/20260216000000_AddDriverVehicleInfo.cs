using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDriverVehicleInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VehiclePlate",
                schema: "identity",
                table: "Users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehicleModel",
                schema: "identity",
                table: "Users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehicleColor",
                schema: "identity",
                table: "Users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehicleType",
                schema: "identity",
                table: "Users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "VehiclePlate", schema: "identity", table: "Users");
            migrationBuilder.DropColumn(name: "VehicleModel", schema: "identity", table: "Users");
            migrationBuilder.DropColumn(name: "VehicleColor", schema: "identity", table: "Users");
            migrationBuilder.DropColumn(name: "VehicleType", schema: "identity", table: "Users");
        }
    }
}
