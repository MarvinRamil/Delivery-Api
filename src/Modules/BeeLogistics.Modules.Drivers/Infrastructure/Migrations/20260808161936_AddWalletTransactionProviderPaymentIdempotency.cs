using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletTransactionProviderPaymentIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderPaymentId",
                schema: "drivers",
                table: "WalletTransactions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            // Backfill from the descriptions the old guard keyed on. This is not cosmetic: the
            // credit path now looks up by ProviderPaymentId, so leaving historical rows null
            // would make a redelivered webhook for an existing top-up find nothing and credit the
            // wallet a second time. The exact wordings below are the ones the writers used —
            // see ProcessDriverTopUpWebhookCommandHandler and
            // RecordDriverTopUpPaymentFailureCommandHandler.
            migrationBuilder.Sql("""
                UPDATE drivers."WalletTransactions"
                SET "ProviderPaymentId" = substring("Description" from '^Top-up via Xendit invoice (.+)$')
                WHERE "ProviderPaymentId" IS NULL
                  AND "Description" LIKE 'Top-up via Xendit invoice %';
                """);

            migrationBuilder.Sql("""
                UPDATE drivers."WalletTransactions"
                SET "ProviderPaymentId" = substring("Description" from '^Top-up via [^ ]+ checkout (.+)$')
                WHERE "ProviderPaymentId" IS NULL
                  AND "Description" LIKE 'Top-up via % checkout %';
                """);

            migrationBuilder.Sql("""
                UPDATE drivers."WalletTransactions"
                SET "ProviderPaymentId" = substring("Description" from '^Top-up payment declined \([^ ]+ ([^)]+)\)')
                WHERE "ProviderPaymentId" IS NULL
                  AND "Description" LIKE 'Top-up payment declined (%';
                """);

            // Anything longer than the column is not a usable key; leave it null rather than
            // truncating it into a value that could collide with a real one.
            migrationBuilder.Sql("""
                UPDATE drivers."WalletTransactions"
                SET "ProviderPaymentId" = NULL
                WHERE "ProviderPaymentId" IS NOT NULL
                  AND (length("ProviderPaymentId") > 128 OR btrim("ProviderPaymentId") = '');
                """);

            // The dedup soft-delete and the unique index that depends on it were split out into
            // AddWalletTransactionProviderPaymentIndex, deployed separately once production's
            // duplicates have been reviewed. This migration only backfills.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderPaymentId",
                schema: "drivers",
                table: "WalletTransactions");
        }
    }
}
