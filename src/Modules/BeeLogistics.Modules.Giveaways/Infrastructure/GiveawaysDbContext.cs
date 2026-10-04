using BeeLogistics.Modules.Giveaways.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Giveaways.Infrastructure;

public class GiveawaysDbContext : DbContext
{
    public GiveawaysDbContext(DbContextOptions<GiveawaysDbContext> options) : base(options)
    {
    }

    public DbSet<Giveaway> Giveaways => Set<Giveaway>();
    public DbSet<GiveawayEntry> GiveawayEntries => Set<GiveawayEntry>();
    public DbSet<GiveawayPrize> GiveawayPrizes => Set<GiveawayPrize>();
    public DbSet<GiveawayWinner> GiveawayWinners => Set<GiveawayWinner>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("giveaways");

        modelBuilder.Entity<Giveaway>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.ImagePath).HasMaxLength(500);
            entity.Property(e => e.RewardDetails).HasMaxLength(500);
            entity.Property(e => e.DtiPermitNumber).HasMaxLength(150);
            entity.Property(e => e.DtiPermitImagePath).HasMaxLength(500);
            entity.HasIndex(e => e.StartDate);
            entity.HasIndex(e => e.EndDate);
            entity.HasIndex(e => e.IsActive);
            entity.HasIndex(e => e.Status);
            entity.HasQueryFilter(e => !e.IsDeleted);

            entity.HasMany(e => e.Prizes)
                .WithOne(p => p.Giveaway)
                .HasForeignKey(p => p.GiveawayId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GiveawayPrize>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.HasIndex(e => new { e.GiveawayId, e.Tier });
        });

        modelBuilder.Entity<GiveawayWinner>(entity =>
        {
            entity.HasKey(e => e.Id);
            
            entity.HasOne(e => e.Giveaway)
                .WithMany()
                .HasForeignKey(e => e.GiveawayId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Prize)
                .WithMany()
                .HasForeignKey(e => e.GiveawayPrizeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => e.GiveawayId);
            entity.HasIndex(e => e.DriverId);
        });

        modelBuilder.Entity<GiveawayEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Giveaway)
                .WithMany()
                .HasForeignKey(e => e.GiveawayId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => e.GiveawayId);
            entity.HasIndex(e => e.DriverId);
            
            // Replaces the old {GiveawayId, DriverId} unique index.
            // Postgres unique constraint on {GiveawayId, DriverId, BookingId} allows multiple rows
            // where BookingId is null, but enforces uniqueness when BookingId has a value (idempotency for deliveries).
            entity.HasIndex(e => new { e.GiveawayId, e.DriverId, e.BookingId }).IsUnique();
            
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<Campaign>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).IsRequired();
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Body).HasMaxLength(4000);
            entity.Property(e => e.ImagePath).HasMaxLength(500);
            entity.Property(e => e.CtaText).HasMaxLength(120);
            entity.Property(e => e.CtaRoute).HasMaxLength(300);
            entity.HasIndex(e => e.StartDate);
            entity.HasIndex(e => e.EndDate);
            entity.HasIndex(e => e.IsActive);
            entity.HasIndex(e => e.Type);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });
    }
}
