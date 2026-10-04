using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Bookings.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingPulseSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NextPulseAt",
                schema: "sales",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PulseCount",
                schema: "sales",
                table: "Bookings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_NextPulseAt",
                schema: "sales",
                table: "Bookings",
                column: "NextPulseAt",
                filter: "\"AssignmentStatus\" = 'BroadcastingToDrivers'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Bookings_NextPulseAt",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "NextPulseAt",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PulseCount",
                schema: "sales",
                table: "Bookings");
        }
    }
}
