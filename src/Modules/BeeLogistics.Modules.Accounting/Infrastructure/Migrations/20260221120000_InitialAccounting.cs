using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Accounting.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialAccounting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "accounting");

            migrationBuilder.CreateTable(
                name: "LedgerEntries",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsDebit = table.Column<bool>(type: "boolean", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ReferenceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReferenceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_CreatedAtUtc",
                schema: "accounting",
                table: "LedgerEntries",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_IdempotencyKey",
                schema: "accounting",
                table: "LedgerEntries",
                column: "IdempotencyKey");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_ReferenceType_ReferenceId",
                schema: "accounting",
                table: "LedgerEntries",
                columns: new[] { "ReferenceType", "ReferenceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LedgerEntries",
                schema: "accounting");
        }
    }
}
