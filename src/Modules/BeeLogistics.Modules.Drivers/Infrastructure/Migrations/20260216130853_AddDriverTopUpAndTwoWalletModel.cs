using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDriverTopUpAndTwoWalletModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Bucket",
                schema: "drivers",
                table: "WalletTransactions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "CashJobBlockThresholdOverride",
                schema: "drivers",
                table: "DriverWallets",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TopUpBalance",
                schema: "drivers",
                table: "DriverWallets",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TopUpNegativeLimitOverride",
                schema: "drivers",
                table: "DriverWallets",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DriverTopUps",
                schema: "drivers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: false),
                    WalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    XenditInvoiceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    XenditInvoiceUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReconciledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverTopUps", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_Bucket",
                schema: "drivers",
                table: "WalletTransactions",
                column: "Bucket");

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_Type",
                schema: "drivers",
                table: "WalletTransactions",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_DriverTopUps_CreatedAt",
                schema: "drivers",
                table: "DriverTopUps",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DriverTopUps_DriverId",
                schema: "drivers",
                table: "DriverTopUps",
                column: "DriverId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverTopUps_ExternalId",
                schema: "drivers",
                table: "DriverTopUps",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriverTopUps_Status",
                schema: "drivers",
                table: "DriverTopUps",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DriverTopUps_WalletId",
                schema: "drivers",
                table: "DriverTopUps",
                column: "WalletId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverTopUps_XenditInvoiceId",
                schema: "drivers",
                table: "DriverTopUps",
                column: "XenditInvoiceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DriverTopUps",
                schema: "drivers");

            migrationBuilder.DropIndex(
                name: "IX_WalletTransactions_Bucket",
                schema: "drivers",
                table: "WalletTransactions");

            migrationBuilder.DropIndex(
                name: "IX_WalletTransactions_Type",
                schema: "drivers",
                table: "WalletTransactions");

            migrationBuilder.DropColumn(
                name: "Bucket",
                schema: "drivers",
                table: "WalletTransactions");

            migrationBuilder.DropColumn(
                name: "CashJobBlockThresholdOverride",
                schema: "drivers",
                table: "DriverWallets");

            migrationBuilder.DropColumn(
                name: "TopUpBalance",
                schema: "drivers",
                table: "DriverWallets");

            migrationBuilder.DropColumn(
                name: "TopUpNegativeLimitOverride",
                schema: "drivers",
                table: "DriverWallets");
        }
    }
}
