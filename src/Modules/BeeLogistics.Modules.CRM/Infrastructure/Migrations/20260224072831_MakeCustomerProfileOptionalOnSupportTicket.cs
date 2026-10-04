using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.CRM.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakeCustomerProfileOptionalOnSupportTicket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SupportTickets_CustomerProfiles_CustomerProfileId",
                schema: "crm",
                table: "SupportTickets");

            migrationBuilder.AlterColumn<Guid>(
                name: "CustomerProfileId",
                schema: "crm",
                table: "SupportTickets",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "UserEmail",
                schema: "crm",
                table: "SupportTickets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserFullName",
                schema: "crm",
                table: "SupportTickets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                schema: "crm",
                table: "SupportTickets",
                type: "text",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SupportTickets_CustomerProfiles_CustomerProfileId",
                schema: "crm",
                table: "SupportTickets",
                column: "CustomerProfileId",
                principalSchema: "crm",
                principalTable: "CustomerProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SupportTickets_CustomerProfiles_CustomerProfileId",
                schema: "crm",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "UserEmail",
                schema: "crm",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "UserFullName",
                schema: "crm",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "UserId",
                schema: "crm",
                table: "SupportTickets");

            migrationBuilder.AlterColumn<Guid>(
                name: "CustomerProfileId",
                schema: "crm",
                table: "SupportTickets",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SupportTickets_CustomerProfiles_CustomerProfileId",
                schema: "crm",
                table: "SupportTickets",
                column: "CustomerProfileId",
                principalSchema: "crm",
                principalTable: "CustomerProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
