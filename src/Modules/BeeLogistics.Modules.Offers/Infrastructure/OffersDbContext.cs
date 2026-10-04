using BeeLogistics.Modules.Offers.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Offers.Infrastructure;

public class OffersDbContext : DbContext
{
    public OffersDbContext(DbContextOptions<OffersDbContext> options) : base(options)
    {
    }

    public DbSet<Offer> Offers => Set<Offer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("offers");

        modelBuilder.Entity<Offer>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.DiscountValue).HasPrecision(10, 2);
            entity.Property(e => e.PromoCode).HasMaxLength(50);
            entity.Property(e => e.ImagePath).HasMaxLength(500);
            entity.HasIndex(e => e.IsActive);
            entity.HasIndex(e => e.TargetAudience);
            entity.HasIndex(e => e.StartsAt);
            entity.HasIndex(e => e.EndsAt);
            entity.HasIndex(e => e.PromoCode);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });
    }
}
