using Domain = BeeLogistics.Modules.Rating.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Rating.Infrastructure;

public class RatingDbContext : DbContext
{
    public RatingDbContext(DbContextOptions<RatingDbContext> options) : base(options)
    {
    }

    public DbSet<Domain.Rating> Ratings { get; set; }
    public DbSet<Domain.DriverRating> DriverRatings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("rating");

        modelBuilder.Entity<Domain.Rating>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DriverId).IsRequired(); // FK to ApplicationUser.Id (Guid, converted from string)
            entity.Property(e => e.CustomerId).IsRequired(); // FK to Customer.Id (Guid)
            entity.Property(e => e.Stars).IsRequired();
            entity.Property(e => e.Comment).HasMaxLength(1000);
            entity.Property(e => e.Category)
                .HasConversion<string>()
                .HasMaxLength(30);

            entity.HasIndex(e => e.BookingId).IsUnique(); // One rating per booking
            entity.HasIndex(e => e.DriverId);
            entity.HasIndex(e => e.CustomerId);
            entity.HasIndex(e => e.RatedAt);
        });

        modelBuilder.Entity<Domain.DriverRating>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DriverId).IsRequired(); // FK to ApplicationUser.Id (Guid, converted from string)
            entity.HasIndex(e => e.DriverId).IsUnique();
            entity.Property(e => e.AverageRating).HasPrecision(3, 2);
        });
    }
}
