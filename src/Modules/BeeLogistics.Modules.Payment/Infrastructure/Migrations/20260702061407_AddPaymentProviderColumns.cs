using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Payment.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentProviderColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "XenditPaymentMethodId",
                schema: "payment",
                table: "SavedPaymentMethods",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.AlterColumn<string>(
                name: "XenditCustomerId",
                schema: "payment",
                table: "SavedPaymentMethods",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                schema: "payment",
                table: "SavedPaymentMethods",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "xendit");

            migrationBuilder.AddColumn<string>(
                name: "ProviderCustomerId",
                schema: "payment",
                table: "SavedPaymentMethods",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProviderPaymentMethodId",
                schema: "payment",
                table: "SavedPaymentMethods",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                schema: "payment",
                table: "Payments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "xendit");

            migrationBuilder.AddColumn<string>(
                name: "ProviderCaptureId",
                schema: "payment",
                table: "Payments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderCheckoutUrl",
                schema: "payment",
                table: "Payments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderPaymentId",
                schema: "payment",
                table: "Payments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderRefundId",
                schema: "payment",
                table: "Payments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // Backfill: every pre-existing record belongs to Xendit. The token columns on
            // SavedPaymentMethods are ciphertext, but old and new columns share the same
            // encryption purpose/key, so a straight column copy stays decryptable.
            migrationBuilder.Sql("""
                UPDATE payment."Payments" SET
                    "Provider" = 'xendit',
                    "ProviderPaymentId" = "XenditInvoiceId",
                    "ProviderCheckoutUrl" = "XenditInvoiceUrl",
                    "ProviderCaptureId" = "XenditPaymentRequestId",
                    "ProviderRefundId" = "XenditRefundId";
                """);

            migrationBuilder.Sql("""
                UPDATE payment."SavedPaymentMethods" SET
                    "Provider" = 'xendit',
                    "ProviderCustomerId" = COALESCE("XenditCustomerId", ''),
                    "ProviderPaymentMethodId" = COALESCE("XenditPaymentMethodId", '');
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Provider_ProviderPaymentId",
                schema: "payment",
                table: "Payments",
                columns: new[] { "Provider", "ProviderPaymentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Provider_ProviderRefundId",
                schema: "payment",
                table: "Payments",
                columns: new[] { "Provider", "ProviderRefundId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_Provider_ProviderPaymentId",
                schema: "payment",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_Provider_ProviderRefundId",
                schema: "payment",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Provider",
                schema: "payment",
                table: "SavedPaymentMethods");

            migrationBuilder.DropColumn(
                name: "ProviderCustomerId",
                schema: "payment",
                table: "SavedPaymentMethods");

            migrationBuilder.DropColumn(
                name: "ProviderPaymentMethodId",
                schema: "payment",
                table: "SavedPaymentMethods");

            migrationBuilder.DropColumn(
                name: "Provider",
                schema: "payment",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ProviderCaptureId",
                schema: "payment",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ProviderCheckoutUrl",
                schema: "payment",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ProviderPaymentId",
                schema: "payment",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ProviderRefundId",
                schema: "payment",
                table: "Payments");

            migrationBuilder.AlterColumn<string>(
                name: "XenditPaymentMethodId",
                schema: "payment",
                table: "SavedPaymentMethods",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "XenditCustomerId",
                schema: "payment",
                table: "SavedPaymentMethods",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }
    }
}
