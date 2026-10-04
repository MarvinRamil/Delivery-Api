using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftDeleteAndAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "identity",
                table: "RefreshTokens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "identity",
                table: "RefreshTokens",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "identity",
                table: "RefreshTokens",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UserEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UserRole = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    EntityId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    IsSuccess = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Category",
                schema: "identity",
                table: "AuditLogs",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_IsArchived",
                schema: "identity",
                table: "AuditLogs",
                column: "IsArchived");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Timestamp",
                schema: "identity",
                table: "AuditLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_UserId",
                schema: "identity",
                table: "AuditLogs",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "identity",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "identity",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "identity",
                table: "RefreshTokens");
        }
    }
}
