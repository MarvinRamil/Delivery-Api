using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDriverCashBond : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CashBondBalance",
                schema: "drivers",
                table: "DriverWallets",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "DriverCashBondConfigs",
                schema: "drivers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverCashBondConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DriverCashBondConfigVersions",
                schema: "drivers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverCashBondConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangedByUserName = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverCashBondConfigVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DriverCashBondConfigVersions_DriverCashBondConfigs_DriverCashBondConfigId",
                        column: x => x.DriverCashBondConfigId,
                        principalSchema: "drivers",
                        principalTable: "DriverCashBondConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DriverCashBondConfigs_VehicleType",
                schema: "drivers",
                table: "DriverCashBondConfigs",
                column: "VehicleType",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriverCashBondConfigs_Version",
                schema: "drivers",
                table: "DriverCashBondConfigs",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_DriverCashBondConfigVersions_CreatedAt",
                schema: "drivers",
                table: "DriverCashBondConfigVersions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DriverCashBondConfigVersions_DriverCashBondConfigId",
                schema: "drivers",
                table: "DriverCashBondConfigVersions",
                column: "DriverCashBondConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverCashBondConfigVersions_DriverCashBondConfigId_Version",
                schema: "drivers",
                table: "DriverCashBondConfigVersions",
                columns: new[] { "DriverCashBondConfigId", "Version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DriverCashBondConfigVersions",
                schema: "drivers");

            migrationBuilder.DropTable(
                name: "DriverCashBondConfigs",
                schema: "drivers");

            migrationBuilder.DropColumn(
                name: "CashBondBalance",
                schema: "drivers",
                table: "DriverWallets");
        }
    }
}
