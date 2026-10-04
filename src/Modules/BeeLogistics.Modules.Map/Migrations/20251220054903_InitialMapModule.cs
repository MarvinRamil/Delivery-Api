using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Map.Migrations
{
    /// <inheritdoc />
    public partial class InitialMapModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "map");

            migrationBuilder.CreateTable(
                name: "DriverLocations",
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
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverLocations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DriverLocations_DriverId",
                schema: "map",
                table: "DriverLocations",
                column: "DriverId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverLocations_DriverId_Timestamp",
                schema: "map",
                table: "DriverLocations",
                columns: new[] { "DriverId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_DriverLocations_Timestamp",
                schema: "map",
                table: "DriverLocations",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DriverLocations",
                schema: "map");
        }
    }
}
