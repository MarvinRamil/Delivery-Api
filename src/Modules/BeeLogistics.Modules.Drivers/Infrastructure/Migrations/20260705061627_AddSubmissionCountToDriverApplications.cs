using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSubmissionCountToDriverApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SubmissionCount",
                schema: "drivers",
                table: "DriverApplications",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubmissionCount",
                schema: "drivers",
                table: "DriverApplications");
        }
    }
}
