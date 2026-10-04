using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Payment.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialPayment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "payment");

            migrationBuilder.CreateTable(
                name: "Payments",
                schema: "payment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    XenditInvoiceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    XenditInvoiceUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_BookingId",
                schema: "payment",
                table: "Payments",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PaymentNumber",
                schema: "payment",
                table: "Payments",
                column: "PaymentNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_XenditInvoiceId",
                schema: "payment",
                table: "Payments",
                column: "XenditInvoiceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Payments",
                schema: "payment");
        }
    }
}
