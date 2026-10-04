using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailBlindIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmailHash",
                schema: "drivers",
                table: "DriverApplications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriverApplications_EmailHash",
                schema: "drivers",
                table: "DriverApplications",
                column: "EmailHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DriverApplications_EmailHash",
                schema: "drivers",
                table: "DriverApplications");

            migrationBuilder.DropColumn(
                name: "EmailHash",
                schema: "drivers",
                table: "DriverApplications");
        }
    }
}
