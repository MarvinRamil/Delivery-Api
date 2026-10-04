using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClerkUserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClerkUserId",
                schema: "identity",
                table: "Users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_ClerkUserId",
                schema: "identity",
                table: "Users",
                column: "ClerkUserId",
                unique: true,
                filter: "\"ClerkUserId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_ClerkUserId",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ClerkUserId",
                schema: "identity",
                table: "Users");
        }
    }
}
