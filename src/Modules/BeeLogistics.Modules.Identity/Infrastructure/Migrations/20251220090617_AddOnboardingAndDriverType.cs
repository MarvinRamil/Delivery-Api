using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardingAndDriverType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BusinessType",
                schema: "identity",
                table: "Users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsOnboarded",
                schema: "identity",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSoloDriver",
                schema: "identity",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Users_BusinessType",
                schema: "identity",
                table: "Users",
                column: "BusinessType");

            migrationBuilder.CreateIndex(
                name: "IX_Users_IsOnboarded",
                schema: "identity",
                table: "Users",
                column: "IsOnboarded");

            migrationBuilder.CreateIndex(
                name: "IX_Users_IsSoloDriver",
                schema: "identity",
                table: "Users",
                column: "IsSoloDriver");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_BusinessType",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_IsOnboarded",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_IsSoloDriver",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BusinessType",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsOnboarded",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsSoloDriver",
                schema: "identity",
                table: "Users");
        }
    }
}
