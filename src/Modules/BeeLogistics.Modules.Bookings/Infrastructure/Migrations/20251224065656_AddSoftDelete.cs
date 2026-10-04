using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Bookings.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "sales",
                table: "DriverBookingOffers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "sales",
                table: "DriverBookingOffers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "sales",
                table: "DriverBookingOffers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "sales",
                table: "Customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "sales",
                table: "Customers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "sales",
                table: "Customers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "sales",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "sales",
                table: "Bookings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "sales",
                table: "Bookings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "sales",
                table: "DriverBookingOffers");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "sales",
                table: "DriverBookingOffers");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "sales",
                table: "DriverBookingOffers");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "sales",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "sales",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "sales",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "sales",
                table: "Bookings");
        }
    }
}
