using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "drivers",
                table: "WithdrawalRequests");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "drivers",
                table: "WalletTransactions");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "drivers",
                table: "SavedWithdrawalMethods");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "drivers",
                table: "GlobalMissions");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "drivers",
                table: "DriverWallets");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "drivers",
                table: "DriverTopUps");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "drivers",
                table: "DriverMissions");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "drivers",
                table: "DriverApplications");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "drivers",
                table: "WithdrawalRequests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "drivers",
                table: "WalletTransactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "drivers",
                table: "SavedWithdrawalMethods",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "drivers",
                table: "GlobalMissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "drivers",
                table: "DriverWallets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "drivers",
                table: "DriverTopUps",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "drivers",
                table: "DriverMissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "drivers",
                table: "DriverApplications",
                type: "uuid",
                nullable: true);
        }
    }
}
