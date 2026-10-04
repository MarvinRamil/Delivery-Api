using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTopUpIdempotencyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                schema: "drivers",
                table: "DriverTopUps",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriverTopUps_DriverId_IdempotencyKey",
                schema: "drivers",
                table: "DriverTopUps",
                columns: new[] { "DriverId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DriverTopUps_DriverId_IdempotencyKey",
                schema: "drivers",
                table: "DriverTopUps");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                schema: "drivers",
                table: "DriverTopUps");
        }
    }
}
