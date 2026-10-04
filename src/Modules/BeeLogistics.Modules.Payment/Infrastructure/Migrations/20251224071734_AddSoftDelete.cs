using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Payment.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "payment",
                table: "Payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "payment",
                table: "Payments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "payment",
                table: "Payments",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "payment",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "payment",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "payment",
                table: "Payments");
        }
    }
}
