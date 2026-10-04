using BeeLogistics.Modules.Accounting.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Accounting.Infrastructure;

public class AccountingDbContext : DbContext
{
    public AccountingDbContext(DbContextOptions<AccountingDbContext> options) : base(options) { }

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<SalesEntry> SalesEntries => Set<SalesEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("accounting");

        modelBuilder.Entity<LedgerEntry>(entity =>
        {
            entity.ToTable("LedgerEntries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AccountCode).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.ReferenceType).IsRequired().HasMaxLength(64);
            entity.Property(e => e.ReferenceId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.IdempotencyKey).HasMaxLength(256);
            entity.Property(e => e.Amount).HasPrecision(18, 2);

            entity.HasIndex(e => new { e.ReferenceType, e.ReferenceId });
            entity.HasIndex(e => e.CreatedAtUtc);

            // Filtered unique: most rows in a reversing/paired posting carry idempotencyKey: null
            // on the second entry (see SaleRecordedAccountingConsumer, WithdrawalFailedAccountingConsumer
            // et al.), and a plain unique index would still allow those to coexist since Postgres
            // never treats two NULLs as equal - but making the filter explicit documents the
            // intent instead of relying on that incidentally. Backs what used to be only an
            // application-level check-then-insert, the same bug class already fixed on the
            // driver-wallet debit path.
            entity.HasIndex(e => e.IdempotencyKey)
                .IsUnique()
                .HasFilter("\"IdempotencyKey\" IS NOT NULL");
        });

        modelBuilder.Entity<SalesEntry>(entity =>
        {
            entity.ToTable("SalesEntries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.PaymentMethod).IsRequired().HasMaxLength(32);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.PlatformCommissionAmount).HasPrecision(18, 2);
            entity.Property(e => e.DriverAmount).HasPrecision(18, 2);
            entity.HasIndex(e => e.BookingId).IsUnique();
            entity.HasIndex(e => e.CompletedAtUtc);
            entity.HasIndex(e => e.PaymentMethod);
        });
    }
}
