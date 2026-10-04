using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Drivers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSavedWithdrawalMethods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SavedWithdrawalMethods",
                schema: "drivers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: false),
                    BankName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BankCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AccountNumber = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    AccountHolderName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
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
                    table.PrimaryKey("PK_SavedWithdrawalMethods", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SavedWithdrawalMethods_DriverId",
                schema: "drivers",
                table: "SavedWithdrawalMethods",
                column: "DriverId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedWithdrawalMethods_DriverId_IsActive",
                schema: "drivers",
                table: "SavedWithdrawalMethods",
                columns: new[] { "DriverId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedWithdrawalMethods_DriverId_IsDefault",
                schema: "drivers",
                table: "SavedWithdrawalMethods",
                columns: new[] { "DriverId", "IsDefault" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavedWithdrawalMethods",
                schema: "drivers");
        }
    }
}
