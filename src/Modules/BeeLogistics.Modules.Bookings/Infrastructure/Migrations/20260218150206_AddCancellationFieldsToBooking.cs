using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Bookings.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCancellationFieldsToBooking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                schema: "sales",
                table: "Bookings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                schema: "sales",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CancelledBy",
                schema: "sales",
                table: "Bookings",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationReason",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CancelledBy",
                schema: "sales",
                table: "Bookings");
        }
    }
}
