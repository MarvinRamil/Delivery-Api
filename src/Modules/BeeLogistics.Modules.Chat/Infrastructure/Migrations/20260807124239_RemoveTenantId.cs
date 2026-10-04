using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Chat.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "chat",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "chat",
                table: "Conversations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "chat",
                table: "Messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "chat",
                table: "Conversations",
                type: "uuid",
                nullable: true);
        }
    }
}
