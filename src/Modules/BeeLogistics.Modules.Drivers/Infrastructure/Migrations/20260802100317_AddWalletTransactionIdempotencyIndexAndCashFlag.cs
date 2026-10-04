using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletTransactionIdempotencyIndexAndCashFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCashEarning",
                schema: "drivers",
                table: "WalletTransactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill from the convention the balance calculation used to rely on: cash
            // earnings were identified by "(cash)" appearing in the free-text description.
            migrationBuilder.Sql("""
                UPDATE drivers."WalletTransactions"
                SET "IsCashEarning" = true
                WHERE "Type" = 0
                  AND "Description" LIKE '%(cash)%';
                """);

            // The unique index below cannot be created while duplicates exist, and duplicates do
            // exist: the cash path looked up one description and stored a different one, so its
            // idempotency guard never matched and every redelivery appended another row.
            //
            // Duplicates are soft-deleted rather than removed - the history stays auditable, and
            // the index filter excludes IsDeleted rows. The earliest row per
            // (WalletId, RelatedBookingId, Type) is kept.
            migrationBuilder.Sql("""
                UPDATE drivers."WalletTransactions" t
                SET "IsDeleted" = true,
                    "DeletedAt" = (NOW() AT TIME ZONE 'UTC'),
                    "DeletedBy" = 'migration:AddWalletTransactionIdempotencyIndexAndCashFlag'
                WHERE t."RelatedBookingId" IS NOT NULL
                  AND t."IsDeleted" = false
                  AND t."Id" <> (
                        SELECT keeper."Id"
                        FROM drivers."WalletTransactions" keeper
                        WHERE keeper."WalletId" = t."WalletId"
                          AND keeper."RelatedBookingId" = t."RelatedBookingId"
                          AND keeper."Type" = t."Type"
                          AND keeper."IsDeleted" = false
                        ORDER BY keeper."CreatedAt", keeper."Id"
                        LIMIT 1
                  );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_WalletId_RelatedBookingId_Type",
                schema: "drivers",
                table: "WalletTransactions",
                columns: new[] { "WalletId", "RelatedBookingId", "Type" },
                unique: true,
                filter: "\"RelatedBookingId\" IS NOT NULL AND \"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WalletTransactions_WalletId_RelatedBookingId_Type",
                schema: "drivers",
                table: "WalletTransactions");

            migrationBuilder.DropColumn(
                name: "IsCashEarning",
                schema: "drivers",
                table: "WalletTransactions");
        }
    }
}
