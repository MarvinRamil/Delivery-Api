using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Referrals.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "referrals",
                table: "UserPoints");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "referrals",
                table: "Referrals");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "referrals",
                table: "ReferralCodes");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "referrals",
                table: "PointsTransactions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "referrals",
                table: "UserPoints",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "referrals",
                table: "Referrals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "referrals",
                table: "ReferralCodes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "referrals",
                table: "PointsTransactions",
                type: "uuid",
                nullable: true);
        }
    }
}
