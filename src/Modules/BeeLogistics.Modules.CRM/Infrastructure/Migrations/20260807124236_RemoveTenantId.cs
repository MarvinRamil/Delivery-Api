using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.CRM.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "crm",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "crm",
                table: "FaqCategories");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "crm",
                table: "FaqArticles");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "crm",
                table: "CustomerProfiles");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "crm",
                table: "CustomerNotes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "crm",
                table: "SupportTickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "crm",
                table: "FaqCategories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "crm",
                table: "FaqArticles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "crm",
                table: "CustomerProfiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "crm",
                table: "CustomerNotes",
                type: "uuid",
                nullable: true);
        }
    }
}
