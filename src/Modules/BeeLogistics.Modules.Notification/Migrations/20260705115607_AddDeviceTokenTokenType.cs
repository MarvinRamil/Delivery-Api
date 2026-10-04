using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Notification.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceTokenTokenType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TokenType",
                schema: "notification",
                table: "DeviceTokens",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TokenType",
                schema: "notification",
                table: "DeviceTokens");
        }
    }
}
