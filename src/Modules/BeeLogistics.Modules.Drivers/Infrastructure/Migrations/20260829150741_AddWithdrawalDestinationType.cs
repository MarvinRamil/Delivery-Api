using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWithdrawalDestinationType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // QR Ph payouts carry no account number, so a withdrawal now records how the
            // driver nominated the destination. Every existing row predates QR and is a
            // bank transfer, which defaultValue 0 (BankAccount) backfills.
            migrationBuilder.AddColumn<int>(
                name: "DestinationType",
                schema: "drivers",
                table: "WithdrawalRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "QrId",
                schema: "drivers",
                table: "WithdrawalRequests",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DestinationType",
                schema: "drivers",
                table: "WithdrawalRequests");

            migrationBuilder.DropColumn(
                name: "QrId",
                schema: "drivers",
                table: "WithdrawalRequests");
        }
    }
}
