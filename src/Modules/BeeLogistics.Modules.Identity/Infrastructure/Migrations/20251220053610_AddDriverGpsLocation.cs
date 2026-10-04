using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDriverGpsLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CurrentLatitude",
                schema: "identity",
                table: "Users",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CurrentLongitude",
                schema: "identity",
                table: "Users",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LocationUpdatedAt",
                schema: "identity",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentLatitude",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CurrentLongitude",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LocationUpdatedAt",
                schema: "identity",
                table: "Users");
        }
    }
}
