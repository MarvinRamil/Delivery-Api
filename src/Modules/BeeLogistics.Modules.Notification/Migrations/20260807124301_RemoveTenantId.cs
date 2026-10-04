using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Notification.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "notification",
                table: "DeviceTokens");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "notification",
                table: "DeviceTokens",
                type: "uuid",
                nullable: true);
        }
    }
}
