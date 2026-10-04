using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDriverProviderColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "XenditDisbursementId",
                schema: "drivers",
                table: "WithdrawalRequests",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                schema: "drivers",
                table: "WithdrawalRequests",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "xendit");

            migrationBuilder.AddColumn<string>(
                name: "ProviderDisbursementId",
                schema: "drivers",
                table: "WithdrawalRequests",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                schema: "drivers",
                table: "DriverTopUps",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "xendit");

            migrationBuilder.AddColumn<string>(
                name: "ProviderCheckoutUrl",
                schema: "drivers",
                table: "DriverTopUps",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderPaymentId",
                schema: "drivers",
                table: "DriverTopUps",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            // Backfill: every pre-existing record belongs to Xendit. Must run before the
            // unique (Provider, ProviderPaymentId) index below; uniqueness is guaranteed
            // by the existing unique index on XenditInvoiceId.
            migrationBuilder.Sql("""
                UPDATE drivers."WithdrawalRequests" SET
                    "Provider" = 'xendit',
                    "ProviderDisbursementId" = "XenditDisbursementId";
                """);

            migrationBuilder.Sql("""
                UPDATE drivers."DriverTopUps" SET
                    "Provider" = 'xendit',
                    "ProviderPaymentId" = "XenditInvoiceId",
                    "ProviderCheckoutUrl" = "XenditInvoiceUrl";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawalRequests_Provider_ProviderDisbursementId",
                schema: "drivers",
                table: "WithdrawalRequests",
                columns: new[] { "Provider", "ProviderDisbursementId" });

            migrationBuilder.CreateIndex(
                name: "IX_DriverTopUps_Provider_ProviderPaymentId",
                schema: "drivers",
                table: "DriverTopUps",
                columns: new[] { "Provider", "ProviderPaymentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WithdrawalRequests_Provider_ProviderDisbursementId",
                schema: "drivers",
                table: "WithdrawalRequests");

            migrationBuilder.DropIndex(
                name: "IX_DriverTopUps_Provider_ProviderPaymentId",
                schema: "drivers",
                table: "DriverTopUps");

            migrationBuilder.DropColumn(
                name: "Provider",
                schema: "drivers",
                table: "WithdrawalRequests");

            migrationBuilder.DropColumn(
                name: "ProviderDisbursementId",
                schema: "drivers",
                table: "WithdrawalRequests");

            migrationBuilder.DropColumn(
                name: "Provider",
                schema: "drivers",
                table: "DriverTopUps");

            migrationBuilder.DropColumn(
                name: "ProviderCheckoutUrl",
                schema: "drivers",
                table: "DriverTopUps");

            migrationBuilder.DropColumn(
                name: "ProviderPaymentId",
                schema: "drivers",
                table: "DriverTopUps");

            migrationBuilder.AlterColumn<string>(
                name: "XenditDisbursementId",
                schema: "drivers",
                table: "WithdrawalRequests",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);
        }
    }
}
