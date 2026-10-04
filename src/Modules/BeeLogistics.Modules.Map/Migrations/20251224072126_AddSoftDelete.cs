using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Map.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "map",
                table: "DriverLocations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "map",
                table: "DriverLocations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "map",
                table: "DriverLocations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "map",
                table: "DriverLocations");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "map",
                table: "DriverLocations");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "map",
                table: "DriverLocations");
        }
    }
}
