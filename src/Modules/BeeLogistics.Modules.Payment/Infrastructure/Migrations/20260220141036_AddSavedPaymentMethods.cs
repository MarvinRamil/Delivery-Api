using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Payment.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSavedPaymentMethods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SavedPaymentMethods",
                schema: "payment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    XenditCustomerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    XenditPaymentMethodId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Last4Digits = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    CardBrand = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ExpiryMonth = table.Column<int>(type: "integer", nullable: true),
                    ExpiryYear = table.Column<int>(type: "integer", nullable: true),
                    CardholderName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedPaymentMethods", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SavedPaymentMethods_CustomerId",
                schema: "payment",
                table: "SavedPaymentMethods",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedPaymentMethods_CustomerId_IsActive",
                schema: "payment",
                table: "SavedPaymentMethods",
                columns: new[] { "CustomerId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedPaymentMethods_CustomerId_IsDefault",
                schema: "payment",
                table: "SavedPaymentMethods",
                columns: new[] { "CustomerId", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedPaymentMethods_XenditCustomerId",
                schema: "payment",
                table: "SavedPaymentMethods",
                column: "XenditCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedPaymentMethods_XenditPaymentMethodId",
                schema: "payment",
                table: "SavedPaymentMethods",
                column: "XenditPaymentMethodId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavedPaymentMethods",
                schema: "payment");
        }
    }
}
