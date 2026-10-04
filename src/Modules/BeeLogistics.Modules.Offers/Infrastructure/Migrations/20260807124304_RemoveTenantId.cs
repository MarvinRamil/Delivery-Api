using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Offers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "offers",
                table: "Offers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "offers",
                table: "Offers",
                type: "uuid",
                nullable: true);
        }
    }
}
