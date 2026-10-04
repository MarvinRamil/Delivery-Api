using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Payment.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentTotalRefunded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TotalRefunded",
                schema: "payment",
                table: "Payments",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Backfill already-refunded rows. Leaving them at the 0 default would report their full
            // amount as still refundable, which is precisely the over-refund this column exists to
            // prevent - the new cap would read "nothing refunded yet" for every historical refund.
            //
            // Status 4 = PaymentStatus.Refunded (stored as int). RefundAmount is null on refunds
            // that completed synchronously without passing through RefundPending, so those are
            // treated as full refunds, which is what they were.
            migrationBuilder.Sql("""
                UPDATE payment."Payments"
                SET "TotalRefunded" = COALESCE("RefundAmount", "Amount")
                WHERE "Status" = 4;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TotalRefunded",
                schema: "payment",
                table: "Payments");
        }
    }
}
