using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecurityAnswerHash1",
                schema: "identity",
                table: "Users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecurityAnswerHash2",
                schema: "identity",
                table: "Users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecurityAnswerHash3",
                schema: "identity",
                table: "Users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SecurityQuestionId1",
                schema: "identity",
                table: "Users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SecurityQuestionId2",
                schema: "identity",
                table: "Users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SecurityQuestionId3",
                schema: "identity",
                table: "Users",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecurityAnswerHash1",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SecurityAnswerHash2",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SecurityAnswerHash3",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SecurityQuestionId1",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SecurityQuestionId2",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SecurityQuestionId3",
                schema: "identity",
                table: "Users");
        }
    }
}
