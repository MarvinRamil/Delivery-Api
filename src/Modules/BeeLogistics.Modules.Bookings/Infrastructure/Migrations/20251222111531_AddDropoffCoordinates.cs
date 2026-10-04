using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Bookings.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDropoffCoordinates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DropoffLatitude",
                schema: "sales",
                table: "Bookings",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DropoffLongitude",
                schema: "sales",
                table: "Bookings",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DropoffLatitude",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "DropoffLongitude",
                schema: "sales",
                table: "Bookings");
        }
    }
}
