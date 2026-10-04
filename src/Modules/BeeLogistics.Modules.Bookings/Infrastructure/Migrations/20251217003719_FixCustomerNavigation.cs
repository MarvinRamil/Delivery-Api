using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Bookings.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixCustomerNavigation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_Customers_CustomerId1",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_CustomerId1",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CustomerId1",
                schema: "sales",
                table: "Bookings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId1",
                schema: "sales",
                table: "Bookings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_CustomerId1",
                schema: "sales",
                table: "Bookings",
                column: "CustomerId1");

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_Customers_CustomerId1",
                schema: "sales",
                table: "Bookings",
                column: "CustomerId1",
                principalSchema: "sales",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
