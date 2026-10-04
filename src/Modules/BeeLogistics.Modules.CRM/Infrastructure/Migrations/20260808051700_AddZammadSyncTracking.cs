using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.CRM.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddZammadSyncTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastZammadSyncAttemptAt",
                schema: "crm",
                table: "SupportTickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastZammadSyncError",
                schema: "crm",
                table: "SupportTickets",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ZammadSyncAttempts",
                schema: "crm",
                table: "SupportTickets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_SupportTickets_LastZammadSyncAttemptAt",
                schema: "crm",
                table: "SupportTickets",
                column: "LastZammadSyncAttemptAt",
                filter: "\"ZammadTicketId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupportTickets_LastZammadSyncAttemptAt",
                schema: "crm",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "LastZammadSyncAttemptAt",
                schema: "crm",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "LastZammadSyncError",
                schema: "crm",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "ZammadSyncAttempts",
                schema: "crm",
                table: "SupportTickets");
        }
    }
}
