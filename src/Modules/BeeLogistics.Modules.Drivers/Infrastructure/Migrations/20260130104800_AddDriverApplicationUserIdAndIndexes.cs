using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDriverApplicationUserIdAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UserId",
                schema: "drivers",
                table: "DriverApplications",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriverApplications_UserId",
                schema: "drivers",
                table: "DriverApplications",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverApplications_UserId_Status",
                schema: "drivers",
                table: "DriverApplications",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DriverApplications_UserId",
                schema: "drivers",
                table: "DriverApplications");

            migrationBuilder.DropIndex(
                name: "IX_DriverApplications_UserId_Status",
                schema: "drivers",
                table: "DriverApplications");

            migrationBuilder.DropColumn(
                name: "UserId",
                schema: "drivers",
                table: "DriverApplications");
        }
    }
}
