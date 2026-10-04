using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Bookings.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingDeliveryMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeliveryMode",
                schema: "sales",
                table: "Bookings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Regular");

            migrationBuilder.AddColumn<int>(
                name: "DispatchPriority",
                schema: "sales",
                table: "Bookings",
                type: "integer",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_DeliveryMode",
                schema: "sales",
                table: "Bookings",
                column: "DeliveryMode");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_DispatchPriority_NextPulseAt",
                schema: "sales",
                table: "Bookings",
                columns: new[] { "DispatchPriority", "NextPulseAt" },
                descending: new[] { true, false },
                filter: "\"AssignmentStatus\" = 'BroadcastingToDrivers'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Bookings_DeliveryMode",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_DispatchPriority_NextPulseAt",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "DeliveryMode",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "DispatchPriority",
                schema: "sales",
                table: "Bookings");
        }
    }
}
