using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Payment.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "payment",
                table: "WebhookEvents");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "payment",
                table: "SavedPaymentMethods");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "payment",
                table: "Payments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "payment",
                table: "WebhookEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "payment",
                table: "SavedPaymentMethods",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "payment",
                table: "Payments",
                type: "uuid",
                nullable: true);
        }
    }
}
