using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LalamoveRedesignDrivers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "RelatedDispatchId",
                schema: "drivers",
                table: "WalletTransactions",
                newName: "RelatedBookingId");

            migrationBuilder.RenameIndex(
                name: "IX_WalletTransactions_RelatedDispatchId",
                schema: "drivers",
                table: "WalletTransactions",
                newName: "IX_WalletTransactions_RelatedBookingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "RelatedBookingId",
                schema: "drivers",
                table: "WalletTransactions",
                newName: "RelatedDispatchId");

            migrationBuilder.RenameIndex(
                name: "IX_WalletTransactions_RelatedBookingId",
                schema: "drivers",
                table: "WalletTransactions",
                newName: "IX_WalletTransactions_RelatedDispatchId");
        }
    }
}
