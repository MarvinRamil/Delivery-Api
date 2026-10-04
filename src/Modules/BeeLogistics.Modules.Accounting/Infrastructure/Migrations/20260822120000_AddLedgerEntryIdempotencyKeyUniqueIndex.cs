using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Accounting.Infrastructure.Migrations
{
    public partial class AddLedgerEntryIdempotencyKeyUniqueIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Was a plain, non-unique index. SaleRecordedAccountingConsumer is protected anyway by
            // SalesEntries' own unique BookingId constraint, but the Withdrawal*AccountingConsumers
            // and the new PaymentRefundedAccountingConsumer relied purely on an application-level
            // check-then-insert against this key, with nothing at the DB level backing it - the
            // same bug class already fixed on the driver-wallet debit path (GitLab #66). Filtered on
            // NOT NULL because most reversing/paired postings leave one side's key null by design
            // (see SaleRecordedAccountingConsumer, WithdrawalFailedAccountingConsumer).
            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_IdempotencyKey",
                schema: "accounting",
                table: "LedgerEntries");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_IdempotencyKey",
                schema: "accounting",
                table: "LedgerEntries",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_IdempotencyKey",
                schema: "accounting",
                table: "LedgerEntries");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_IdempotencyKey",
                schema: "accounting",
                table: "LedgerEntries",
                column: "IdempotencyKey");
        }
    }
}
