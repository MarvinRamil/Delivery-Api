using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Revenue.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "revenue",
                table: "PlatformCommissions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "revenue",
                table: "PlatformCommissions",
                type: "uuid",
                nullable: true);
        }
    }
}
