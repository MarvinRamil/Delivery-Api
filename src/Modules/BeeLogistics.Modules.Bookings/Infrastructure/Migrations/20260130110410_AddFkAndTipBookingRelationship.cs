using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Bookings.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFkAndTipBookingRelationship : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tips_Bookings_BookingId",
                schema: "sales",
                table: "Tips");

            migrationBuilder.AddForeignKey(
                name: "FK_Tips_Bookings_BookingId",
                schema: "sales",
                table: "Tips",
                column: "BookingId",
                principalSchema: "sales",
                principalTable: "Bookings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tips_Bookings_BookingId",
                schema: "sales",
                table: "Tips");

            migrationBuilder.AddForeignKey(
                name: "FK_Tips_Bookings_BookingId",
                schema: "sales",
                table: "Tips",
                column: "BookingId",
                principalSchema: "sales",
                principalTable: "Bookings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
