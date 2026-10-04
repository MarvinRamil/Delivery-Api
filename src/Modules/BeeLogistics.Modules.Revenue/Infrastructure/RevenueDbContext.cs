using BeeLogistics.Modules.Revenue.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Revenue.Infrastructure;

public class RevenueDbContext : DbContext
{
    public DbSet<PlatformCommission> PlatformCommissions { get; set; } = null!;

    public RevenueDbContext(DbContextOptions<RevenueDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("revenue");

        modelBuilder.Entity<PlatformCommission>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.BookingId).IsRequired();
            entity.Property(e => e.DriverId).IsRequired();
            entity.Property(e => e.PaymentMethod).IsRequired().HasMaxLength(50);
            entity.Property(e => e.GrossAmount).HasPrecision(18, 2);
            entity.Property(e => e.CommissionRate).HasPrecision(5, 4);
            entity.Property(e => e.CommissionAmount).HasPrecision(18, 2);
            entity.Property(e => e.DriverAmount).HasPrecision(18, 2);
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();

            // One commission record per booking
            entity.HasIndex(e => e.BookingId).IsUnique();

            // Query indices
            entity.HasIndex(e => e.DriverId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
        });
    }
}
