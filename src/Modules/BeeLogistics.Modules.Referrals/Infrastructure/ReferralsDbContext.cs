using BeeLogistics.Modules.Referrals.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Referrals.Infrastructure;

public class ReferralsDbContext : DbContext
{
    public ReferralsDbContext(DbContextOptions<ReferralsDbContext> options) : base(options)
    {
    }

    public DbSet<ReferralCode> ReferralCodes => Set<ReferralCode>();
    public DbSet<Referral> Referrals => Set<Referral>();
    public DbSet<UserPoints> UserPoints => Set<UserPoints>();
    public DbSet<PointsTransaction> PointsTransactions => Set<PointsTransaction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("referrals");

        modelBuilder.Entity<ReferralCode>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Code).IsRequired().HasMaxLength(20);
            entity.Property(e => e.ReferralLink).IsRequired().HasMaxLength(500);
            entity.Property(e => e.UserType).IsRequired();
            
            entity.HasIndex(e => e.Code).IsUnique();
            entity.HasIndex(e => e.UserId).IsUnique(); // one referral code per user
            entity.HasIndex(e => e.IsActive);
            
            entity.HasMany(e => e.Referrals)
                .WithOne(r => r.ReferralCode)
                .HasForeignKey(r => r.ReferralCodeId)
                .OnDelete(DeleteBehavior.Restrict);
            
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<Referral>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ReferrerType).IsRequired();
            entity.Property(e => e.ReferredType).IsRequired();
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.PointsAwarded).HasPrecision(18, 2);
            
            entity.HasIndex(e => e.ReferrerId);
            entity.HasIndex(e => e.ReferredUserId).IsUnique(); // a user can only be referred once
            entity.HasIndex(e => e.ReferralCodeId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => new { e.ReferrerId, e.Status });
            
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<UserPoints>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TotalPoints).HasPrecision(18, 2);
            entity.Property(e => e.AvailablePoints).HasPrecision(18, 2);
            entity.Property(e => e.PendingPoints).HasPrecision(18, 2);
            
            entity.HasIndex(e => e.UserId).IsUnique();
            
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<PointsTransaction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Points).HasPrecision(18, 2);
            entity.Property(e => e.BalanceAfter).HasPrecision(18, 2);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Type).IsRequired();
            
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.TransactionDate);
            entity.HasIndex(e => e.RelatedReferralId);
            entity.HasIndex(e => new { e.UserId, e.TransactionDate });
            
            entity.HasQueryFilter(e => !e.IsDeleted);
        });
    }
}

