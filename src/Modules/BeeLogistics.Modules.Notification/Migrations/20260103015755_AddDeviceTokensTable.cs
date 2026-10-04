using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Notification.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceTokensTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notification");

            migrationBuilder.CreateTable(
                name: "DeviceTokens",
                schema: "notification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Token = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Platform = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AppType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTokens_Token",
                schema: "notification",
                table: "DeviceTokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTokens_UserId_AppType",
                schema: "notification",
                table: "DeviceTokens",
                columns: new[] { "UserId", "AppType" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTokens_UserId_Platform_AppType",
                schema: "notification",
                table: "DeviceTokens",
                columns: new[] { "UserId", "Platform", "AppType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceTokens",
                schema: "notification");
        }
    }
}
