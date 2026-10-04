using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Rating.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "rating",
                table: "Ratings");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "rating",
                table: "DriverRatings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "rating",
                table: "Ratings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "rating",
                table: "DriverRatings",
                type: "uuid",
                nullable: true);
        }
    }
}
