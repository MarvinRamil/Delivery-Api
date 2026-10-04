using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPhoneBlindIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PhoneNumberHash",
                schema: "identity",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_PhoneNumberHash",
                schema: "identity",
                table: "Users",
                column: "PhoneNumberHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_PhoneNumberHash",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PhoneNumberHash",
                schema: "identity",
                table: "Users");
        }
    }
}
