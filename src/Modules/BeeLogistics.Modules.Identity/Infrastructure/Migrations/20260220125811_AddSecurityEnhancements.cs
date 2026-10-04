using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityEnhancements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add UserAgent and RequestId to AuditLogs table
            migrationBuilder.AddColumn<string>(
                name: "UserAgent",
                schema: "identity",
                table: "AuditLogs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestId",
                schema: "identity",
                table: "AuditLogs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            // Add index on RequestId for correlation queries
            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_RequestId",
                schema: "identity",
                table: "AuditLogs",
                column: "RequestId");

            // Add DeviceId and DeviceFingerprint to RefreshTokens table
            migrationBuilder.AddColumn<string>(
                name: "DeviceId",
                schema: "identity",
                table: "RefreshTokens",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceFingerprint",
                schema: "identity",
                table: "RefreshTokens",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            // Add index on DeviceId for device tracking
            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_DeviceId",
                schema: "identity",
                table: "RefreshTokens",
                column: "DeviceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove indexes
            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_DeviceId",
                schema: "identity",
                table: "RefreshTokens");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_RequestId",
                schema: "identity",
                table: "AuditLogs");

            // Remove columns from RefreshTokens
            migrationBuilder.DropColumn(
                name: "DeviceFingerprint",
                schema: "identity",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "DeviceId",
                schema: "identity",
                table: "RefreshTokens");

            // Remove columns from AuditLogs
            migrationBuilder.DropColumn(
                name: "RequestId",
                schema: "identity",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "UserAgent",
                schema: "identity",
                table: "AuditLogs");
        }
    }
}
