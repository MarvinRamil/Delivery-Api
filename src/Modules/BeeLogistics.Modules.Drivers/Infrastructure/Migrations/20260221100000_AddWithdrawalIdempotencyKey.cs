using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWithdrawalIdempotencyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                schema: "drivers",
                table: "WithdrawalRequests",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawalRequests_DriverId_IdempotencyKey",
                schema: "drivers",
                table: "WithdrawalRequests",
                columns: new[] { "DriverId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WithdrawalRequests_DriverId_IdempotencyKey",
                schema: "drivers",
                table: "WithdrawalRequests");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                schema: "drivers",
                table: "WithdrawalRequests");
        }
    }
}
