using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeeLogistics.Modules.Accounting.Infrastructure.Migrations
{
    /// <summary>
    /// Creates accounting schema and moves LedgerEntries from public to accounting
    /// (for DBs where InitialAccounting was applied before schema was introduced).
    /// </summary>
    public partial class MoveLedgerEntriesToAccountingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "accounting");

            // If LedgerEntries exists in public (from initial migration without schema), move it
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.tables
                        WHERE table_schema = 'public' AND table_name = 'LedgerEntries'
                    ) THEN
                        ALTER TABLE public.""LedgerEntries"" SET SCHEMA accounting;
                    END IF;
                END $$;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Move table back to public if needed
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.tables
                        WHERE table_schema = 'accounting' AND table_name = 'LedgerEntries'
                    ) THEN
                        ALTER TABLE accounting.""LedgerEntries"" SET SCHEMA public;
                    END IF;
                END $$;
            ");
        }
    }
}
