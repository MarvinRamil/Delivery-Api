using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Bookings.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Bookings_AssignedToTenantId",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_BeeTenantId",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "VehiclePricingVersions");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "VehiclePricings");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "Tips");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "ProofOfDeliveries");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "FavouriteDrivers");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "DriverBookingOffers");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "DeliveryStops");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "AssignedByUserId",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "AssignedToTenantId",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "BeeTenantId",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sales",
                table: "Bookings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "VehiclePricingVersions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "VehiclePricings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "Tips",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "ProofOfDeliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "FavouriteDrivers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "DriverBookingOffers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "DeliveryStops",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "Customers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssignedByUserId",
                schema: "sales",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssignedToTenantId",
                schema: "sales",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BeeTenantId",
                schema: "sales",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sales",
                table: "Bookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_AssignedToTenantId",
                schema: "sales",
                table: "Bookings",
                column: "AssignedToTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_BeeTenantId",
                schema: "sales",
                table: "Bookings",
                column: "BeeTenantId");
        }
    }
}
