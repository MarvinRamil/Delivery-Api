using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Accounting.Infrastructure.Migrations
{
    public partial class AddSalesEntries : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The accounting schema and an identical SalesEntries table were originally created by
            // hand, by running CreateSalesEntries.sql at the repo root. That left the database with
            // the table but __AccountingMigrationsHistory empty, so EF believes nothing has been
            // applied while the database disagrees - and this CreateTable would fail with
            // "relation already exists" the first time migrations actually run.
            //
            // Drop that orphan, but ONLY when it is empty. If it somehow holds rows, this is a
            // no-op and CreateTable below fails loudly, which is the correct outcome: losing
            // recorded sales silently would be far worse than a failed startup.
            //
            // Safe to edit this already-written migration because EF only runs migrations absent
            // from the history table; any environment that had applied it simply skips this.
            // The row check and the drop go through EXECUTE so they are only parsed at run time.
            // Written inline they would reference accounting."SalesEntries" directly, and on a
            // fresh database - where the table correctly does not exist - PL/pgSQL could raise
            // "relation does not exist" while evaluating the guard, breaking clean installs.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    existing_rows bigint;
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.tables
                        WHERE table_schema = 'accounting' AND table_name = 'SalesEntries'
                    ) THEN
                        EXECUTE 'SELECT count(*) FROM accounting."SalesEntries"' INTO existing_rows;

                        IF existing_rows = 0 THEN
                            EXECUTE 'DROP TABLE accounting."SalesEntries"';
                        END IF;
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateTable(
                name: "SalesEntries",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    PaymentMethod = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformCommissionAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DriverAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesEntries_BookingId",
                schema: "accounting",
                table: "SalesEntries",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesEntries_CompletedAtUtc",
                schema: "accounting",
                table: "SalesEntries",
                column: "CompletedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SalesEntries_PaymentMethod",
                schema: "accounting",
                table: "SalesEntries",
                column: "PaymentMethod");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SalesEntries",
                schema: "accounting");
        }
    }
}
