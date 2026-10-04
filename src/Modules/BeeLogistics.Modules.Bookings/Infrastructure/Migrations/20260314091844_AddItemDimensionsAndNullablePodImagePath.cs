using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Bookings.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddItemDimensionsAndNullablePodImagePath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ItemLengthCm",
                schema: "sales",
                table: "Bookings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ItemWidthCm",
                schema: "sales",
                table: "Bookings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ItemHeightCm",
                schema: "sales",
                table: "Bookings",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ImagePath",
                schema: "sales",
                table: "ProofOfDeliveries",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ItemLengthCm",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ItemWidthCm",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ItemHeightCm",
                schema: "sales",
                table: "Bookings");

            migrationBuilder.AlterColumn<string>(
                name: "ImagePath",
                schema: "sales",
                table: "ProofOfDeliveries",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }
    }
}
