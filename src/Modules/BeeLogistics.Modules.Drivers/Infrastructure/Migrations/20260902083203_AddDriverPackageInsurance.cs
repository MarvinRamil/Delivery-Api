using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDriverPackageInsurance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PolicyYearNumber",
                schema: "drivers",
                table: "WalletTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DriverPackageInsuranceFeeConfigs",
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
                    table.PrimaryKey("PK_DriverPackageInsuranceFeeConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DriverPackageInsurancePolicies",
                schema: "drivers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: false),
                    CoverageStartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaidThroughYearNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverPackageInsurancePolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DriverPackageInsuranceFeeConfigVersions",
                schema: "drivers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverPackageInsuranceFeeConfigId = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_DriverPackageInsuranceFeeConfigVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DriverPackageInsuranceFeeConfigVersions_DriverPackageInsura~",
                        column: x => x.DriverPackageInsuranceFeeConfigId,
                        principalSchema: "drivers",
                        principalTable: "DriverPackageInsuranceFeeConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_WalletId_Type_PolicyYearNumber",
                schema: "drivers",
                table: "WalletTransactions",
                columns: new[] { "WalletId", "Type", "PolicyYearNumber" },
                unique: true,
                filter: "\"PolicyYearNumber\" IS NOT NULL AND \"Status\" <> 2 AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_DriverPackageInsuranceFeeConfigs_VehicleType",
                schema: "drivers",
                table: "DriverPackageInsuranceFeeConfigs",
                column: "VehicleType",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriverPackageInsuranceFeeConfigs_Version",
                schema: "drivers",
                table: "DriverPackageInsuranceFeeConfigs",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_DriverPackageInsuranceFeeConfigVersions_CreatedAt",
                schema: "drivers",
                table: "DriverPackageInsuranceFeeConfigVersions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DriverPackageInsuranceFeeConfigVersions_DriverPackageInsur~1",
                schema: "drivers",
                table: "DriverPackageInsuranceFeeConfigVersions",
                columns: new[] { "DriverPackageInsuranceFeeConfigId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_DriverPackageInsuranceFeeConfigVersions_DriverPackageInsura~",
                schema: "drivers",
                table: "DriverPackageInsuranceFeeConfigVersions",
                column: "DriverPackageInsuranceFeeConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverPackageInsurancePolicies_DriverId",
                schema: "drivers",
                table: "DriverPackageInsurancePolicies",
                column: "DriverId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DriverPackageInsuranceFeeConfigVersions",
                schema: "drivers");

            migrationBuilder.DropTable(
                name: "DriverPackageInsurancePolicies",
                schema: "drivers");

            migrationBuilder.DropTable(
                name: "DriverPackageInsuranceFeeConfigs",
                schema: "drivers");

            migrationBuilder.DropIndex(
                name: "IX_WalletTransactions_WalletId_Type_PolicyYearNumber",
                schema: "drivers",
                table: "WalletTransactions");

            migrationBuilder.DropColumn(
                name: "PolicyYearNumber",
                schema: "drivers",
                table: "WalletTransactions");
        }
    }
}
