using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Chat.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "chat",
                table: "Messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "chat",
                table: "Messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "chat",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "chat",
                table: "Conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "chat",
                table: "Conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "chat",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "chat",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "chat",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "chat",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "chat",
                table: "Conversations");
        }
    }
}
