using BeeLogistics.Modules.Notification.Domain;
using BeeLogistics.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Notification.Infrastructure;

public class NotificationDbContext : DbContext
{
    public NotificationDbContext(DbContextOptions<NotificationDbContext> options)
        : base(options)
    {
    }

    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
    public DbSet<EmailRecord> EmailRecords => Set<EmailRecord>();
    public DbSet<SmsRecord> SmsRecords => Set<SmsRecord>();
    public DbSet<PushNotificationRecord> PushNotificationRecords => Set<PushNotificationRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("notification");

        modelBuilder.Entity<DeviceToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).HasMaxLength(450).IsRequired();
            entity.Property(e => e.Token).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Platform).HasMaxLength(20).IsRequired();
            entity.Property(e => e.AppType).HasMaxLength(20).IsRequired();

            // Index for efficient lookups
            entity.HasIndex(e => new { e.UserId, e.AppType });
            entity.HasIndex(e => e.Token).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.Platform, e.AppType });
        });

        modelBuilder.Entity<EmailRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.To).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Subject).HasMaxLength(500).IsRequired();
            entity.Property(e => e.From).HasMaxLength(500);
            entity.Property(e => e.FromName).HasMaxLength(200);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.Property(e => e.HangfireJobId).HasMaxLength(100);
            entity.Property(e => e.Status).HasConversion<int>();

            // Indexes for efficient queries
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.To);
            entity.HasIndex(e => e.HangfireJobId);
        });

        modelBuilder.Entity<SmsRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.To).HasMaxLength(30).IsRequired();
            entity.Property(e => e.MessageType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Provider).HasMaxLength(50);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.Property(e => e.Status).HasConversion<int>();

            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.To);
        });

        modelBuilder.Entity<PushNotificationRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Body).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.TargetMode).HasMaxLength(20).IsRequired();
            entity.Property(e => e.TargetValue).HasMaxLength(500);
            entity.Property(e => e.AppType).HasMaxLength(20);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.Property(e => e.Source).HasMaxLength(50);
            entity.Property(e => e.Status).HasConversion<int>();

            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.AppType);
        });
    }
}

