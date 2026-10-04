using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Map.Migrations
{
    /// <inheritdoc />
    public partial class IncreaseDeviceIdMaxLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "DeviceId",
                schema: "map",
                table: "DriverLocations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "DeviceId",
                schema: "map",
                table: "LocationHistory",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "DeviceId",
                schema: "map",
                table: "DriverLocations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "DeviceId",
                schema: "map",
                table: "LocationHistory",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }
    }
}
