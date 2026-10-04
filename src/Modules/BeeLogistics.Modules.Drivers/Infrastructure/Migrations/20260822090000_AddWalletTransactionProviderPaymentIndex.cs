using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletTransactionProviderPaymentIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Split out of AddWalletTransactionProviderPaymentIdempotency so the backfill could
            // ship without also soft-deleting rows automatically. Deploy this once production's
            // duplicate ProviderPaymentId rows (if any) have been reviewed -- this still
            // soft-deletes on Up(), it just no longer rides along with the backfill-only change.
            //
            // The unique index cannot be created while duplicates exist, and duplicates are
            // exactly what the missing guard allowed. Soft-delete rather than remove: the history
            // stays auditable and the index filter excludes IsDeleted rows. Earliest row per
            // (WalletId, Type, ProviderPaymentId) is kept, matching the approach
            // AddWalletTransactionIdempotencyIndexAndCashFlag took for booking-linked rows.
            migrationBuilder.Sql("""
                UPDATE drivers."WalletTransactions" t
                SET "IsDeleted" = true,
                    "DeletedAt" = (NOW() AT TIME ZONE 'UTC'),
                    "DeletedBy" = 'migration:AddWalletTransactionProviderPaymentIndex'
                WHERE t."ProviderPaymentId" IS NOT NULL
                  AND t."IsDeleted" = false
                  AND t."Id" <> (
                        SELECT keeper."Id"
                        FROM drivers."WalletTransactions" keeper
                        WHERE keeper."WalletId" = t."WalletId"
                          AND keeper."Type" = t."Type"
                          AND keeper."ProviderPaymentId" = t."ProviderPaymentId"
                          AND keeper."IsDeleted" = false
                        ORDER BY keeper."CreatedAt", keeper."Id"
                        LIMIT 1
                  );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_WalletId_Type_ProviderPaymentId",
                schema: "drivers",
                table: "WalletTransactions",
                columns: new[] { "WalletId", "Type", "ProviderPaymentId" },
                unique: true,
                filter: "\"ProviderPaymentId\" IS NOT NULL AND \"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WalletTransactions_WalletId_Type_ProviderPaymentId",
                schema: "drivers",
                table: "WalletTransactions");
        }
    }
}
