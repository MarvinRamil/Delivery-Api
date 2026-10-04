using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Messaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingRoomEndedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EndedAt",
                schema: "messaging",
                table: "booking_rooms",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_booking_rooms_State_EndedAt",
                schema: "messaging",
                table: "booking_rooms",
                columns: new[] { "State", "EndedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_booking_rooms_State_EndedAt",
                schema: "messaging",
                table: "booking_rooms");

            migrationBuilder.DropColumn(
                name: "EndedAt",
                schema: "messaging",
                table: "booking_rooms");
        }
    }
}
