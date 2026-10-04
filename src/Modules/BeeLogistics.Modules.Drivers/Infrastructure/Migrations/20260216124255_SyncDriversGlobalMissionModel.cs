using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SyncDriversGlobalMissionModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GlobalMissionId",
                schema: "drivers",
                table: "DriverMissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GlobalMissions",
                schema: "drivers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Reward = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Target = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalMissions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DriverMissions_GlobalMissionId",
                schema: "drivers",
                table: "DriverMissions",
                column: "GlobalMissionId");

            migrationBuilder.CreateIndex(
                name: "IX_GlobalMissions_ExpiresAt",
                schema: "drivers",
                table: "GlobalMissions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_GlobalMissions_IsActive",
                schema: "drivers",
                table: "GlobalMissions",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GlobalMissions",
                schema: "drivers");

            migrationBuilder.DropIndex(
                name: "IX_DriverMissions_GlobalMissionId",
                schema: "drivers",
                table: "DriverMissions");

            migrationBuilder.DropColumn(
                name: "GlobalMissionId",
                schema: "drivers",
                table: "DriverMissions");
        }
    }
}
