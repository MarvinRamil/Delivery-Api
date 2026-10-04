using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_BusinessType",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_IsSoloDriver",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BusinessType",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsSoloDriver",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "identity",
                table: "RefreshTokens");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BusinessType",
                schema: "identity",
                table: "Users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                schema: "identity",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSoloDriver",
                schema: "identity",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "identity",
                table: "RefreshTokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_BusinessType",
                schema: "identity",
                table: "Users",
                column: "BusinessType");

            migrationBuilder.CreateIndex(
                name: "IX_Users_IsSoloDriver",
                schema: "identity",
                table: "Users",
                column: "IsSoloDriver");
        }
    }
}
