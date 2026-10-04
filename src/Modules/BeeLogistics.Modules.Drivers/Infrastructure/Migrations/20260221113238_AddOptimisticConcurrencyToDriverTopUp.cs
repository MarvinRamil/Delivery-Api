using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOptimisticConcurrencyToDriverTopUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "IdempotencyKey",
                schema: "drivers",
                table: "DriverTopUps",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "drivers",
                table: "DriverTopUps",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "drivers",
                table: "DriverTopUps");

            migrationBuilder.AlterColumn<string>(
                name: "IdempotencyKey",
                schema: "drivers",
                table: "DriverTopUps",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);
        }
    }
}
