using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.CRM.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "crm",
                table: "SupportTickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "crm",
                table: "SupportTickets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "crm",
                table: "SupportTickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "crm",
                table: "FaqCategories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "crm",
                table: "FaqCategories",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "crm",
                table: "FaqCategories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "crm",
                table: "FaqArticles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "crm",
                table: "FaqArticles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "crm",
                table: "FaqArticles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "crm",
                table: "CustomerProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "crm",
                table: "CustomerProfiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "crm",
                table: "CustomerProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "crm",
                table: "CustomerNotes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "crm",
                table: "CustomerNotes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "crm",
                table: "CustomerNotes",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "crm",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "crm",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "crm",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "crm",
                table: "FaqCategories");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "crm",
                table: "FaqCategories");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "crm",
                table: "FaqCategories");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "crm",
                table: "FaqArticles");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "crm",
                table: "FaqArticles");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "crm",
                table: "FaqArticles");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "crm",
                table: "CustomerProfiles");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "crm",
                table: "CustomerProfiles");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "crm",
                table: "CustomerProfiles");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "crm",
                table: "CustomerNotes");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "crm",
                table: "CustomerNotes");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "crm",
                table: "CustomerNotes");
        }
    }
}
