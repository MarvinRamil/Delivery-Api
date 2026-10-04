using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Payment.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentConcurrencyTokenAndIdempotencyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                schema: "payment",
                table: "Payments",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "payment",
                table: "Payments",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CustomerId_IdempotencyKey",
                schema: "payment",
                table: "Payments",
                columns: new[] { "CustomerId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_CustomerId_IdempotencyKey",
                schema: "payment",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                schema: "payment",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "payment",
                table: "Payments");
        }
    }
}
