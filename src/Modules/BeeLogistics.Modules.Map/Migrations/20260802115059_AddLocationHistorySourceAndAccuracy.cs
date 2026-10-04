using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Map.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationHistorySourceAndAccuracy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Accuracy",
                schema: "map",
                table: "LocationHistory",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHistorical",
                schema: "map",
                table: "LocationHistory",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                schema: "map",
                table: "LocationHistory",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Accuracy",
                schema: "map",
                table: "LocationHistory");

            migrationBuilder.DropColumn(
                name: "IsHistorical",
                schema: "map",
                table: "LocationHistory");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "map",
                table: "LocationHistory");
        }
    }
}
