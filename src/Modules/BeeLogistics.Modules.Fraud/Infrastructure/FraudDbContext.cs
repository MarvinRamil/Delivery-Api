using BeeLogistics.Modules.Fraud.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Fraud.Infrastructure;

public class FraudDbContext : DbContext
{
    public FraudDbContext(DbContextOptions<FraudDbContext> options) : base(options) { }

    public DbSet<FraudEvent> FraudEvents { get; set; }
    public DbSet<FraudSignal> FraudSignals { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("fraud");

        modelBuilder.Entity<FraudEvent>(entity =>
        {
            entity.ToTable("FraudEvents");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.EventType, e.ActorId, e.OccurredAt });
            entity.HasIndex(e => e.OccurredAt);
            entity.HasIndex(e => e.DriverId);
            entity.HasIndex(e => e.CustomerId);
            entity.HasIndex(e => e.DeviceId);
        });

        modelBuilder.Entity<FraudSignal>(entity =>
        {
            entity.ToTable("FraudSignals");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.RuleName, e.DetectedAt });
            entity.HasIndex(e => e.ActorId);
            entity.HasIndex(e => e.DetectedAt);
        });
    }
}
