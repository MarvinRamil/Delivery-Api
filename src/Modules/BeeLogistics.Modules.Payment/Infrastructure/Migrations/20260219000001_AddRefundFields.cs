using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Payment.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "XenditPaymentRequestId",
                schema: "payment",
                table: "Payments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RefundedAt",
                schema: "payment",
                table: "Payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "XenditRefundId",
                schema: "payment",
                table: "Payments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "XenditPaymentRequestId",
                schema: "payment",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RefundedAt",
                schema: "payment",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "XenditRefundId",
                schema: "payment",
                table: "Payments");
        }
    }
}
