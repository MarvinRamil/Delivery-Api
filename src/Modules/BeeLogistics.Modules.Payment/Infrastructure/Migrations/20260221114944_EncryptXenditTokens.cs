using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Payment.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EncryptXenditTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SavedPaymentMethods_XenditPaymentMethodId",
                schema: "payment",
                table: "SavedPaymentMethods");

            migrationBuilder.AlterColumn<string>(
                name: "XenditPaymentMethodId",
                schema: "payment",
                table: "SavedPaymentMethods",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "XenditCustomerId",
                schema: "payment",
                table: "SavedPaymentMethods",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "XenditPaymentMethodId",
                schema: "payment",
                table: "SavedPaymentMethods",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.AlterColumn<string>(
                name: "XenditCustomerId",
                schema: "payment",
                table: "SavedPaymentMethods",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.CreateIndex(
                name: "IX_SavedPaymentMethods_XenditPaymentMethodId",
                schema: "payment",
                table: "SavedPaymentMethods",
                column: "XenditPaymentMethodId",
                unique: true);
        }
    }
}
