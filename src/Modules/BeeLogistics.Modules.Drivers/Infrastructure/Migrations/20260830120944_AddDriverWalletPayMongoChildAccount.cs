using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDriverWalletPayMongoChildAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PayMongoAccountEmail",
                schema: "drivers",
                table: "DriverWallets",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayMongoAccountId",
                schema: "drivers",
                table: "DriverWallets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayMongoAccountNumber",
                schema: "drivers",
                table: "DriverWallets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PayMongoActivationStatus",
                schema: "drivers",
                table: "DriverWallets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PayMongoLedgerAccountId",
                schema: "drivers",
                table: "DriverWallets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayMongoWalletId",
                schema: "drivers",
                table: "DriverWallets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriverWallets_PayMongoAccountId",
                schema: "drivers",
                table: "DriverWallets",
                column: "PayMongoAccountId",
                unique: true,
                filter: "\"PayMongoAccountId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DriverWallets_PayMongoAccountId",
                schema: "drivers",
                table: "DriverWallets");

            migrationBuilder.DropColumn(
                name: "PayMongoAccountEmail",
                schema: "drivers",
                table: "DriverWallets");

            migrationBuilder.DropColumn(
                name: "PayMongoAccountId",
                schema: "drivers",
                table: "DriverWallets");

            migrationBuilder.DropColumn(
                name: "PayMongoAccountNumber",
                schema: "drivers",
                table: "DriverWallets");

            migrationBuilder.DropColumn(
                name: "PayMongoActivationStatus",
                schema: "drivers",
                table: "DriverWallets");

            migrationBuilder.DropColumn(
                name: "PayMongoLedgerAccountId",
                schema: "drivers",
                table: "DriverWallets");

            migrationBuilder.DropColumn(
                name: "PayMongoWalletId",
                schema: "drivers",
                table: "DriverWallets");
        }
    }
}
