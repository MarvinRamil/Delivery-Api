using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDriverOnlineStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsOnline",
                schema: "identity",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Users_IsOnline",
                schema: "identity",
                table: "Users",
                column: "IsOnline");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_IsOnline",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsOnline",
                schema: "identity",
                table: "Users");
        }
    }
}
