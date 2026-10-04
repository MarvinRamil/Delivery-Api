using BeeLogistics.Modules.Verification.Domain;
using BeeLogistics.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Verification.Infrastructure;

public class VerificationDbContext : DbContext
{
    private readonly IDataProtectorService? _dataProtector;

    public VerificationDbContext(DbContextOptions<VerificationDbContext> options, IDataProtectorService? dataProtector = null) : base(options)
    {
        _dataProtector = dataProtector;
    }

    public DbSet<DriverVerification> DriverVerifications => Set<DriverVerification>();
    public DbSet<CustomerVerification> CustomerVerifications => Set<CustomerVerification>();
    public DbSet<ShiftFaceCheck> ShiftFaceChecks => Set<ShiftFaceCheck>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("verification");

        // Value Converter for PII encryption (same pattern as DriversDbContext)
        var nullablePiiConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<string?, string?>(
            v => v != null && _dataProtector != null ? _dataProtector.Encrypt(v, "PII") : v,
            v => v != null && _dataProtector != null ? _dataProtector.Decrypt(v, "PII") : v);

        modelBuilder.Entity<DriverVerification>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450); // FK to ApplicationUser.Id (string)
            entity.Property(e => e.DiditSessionId).HasMaxLength(64);
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.ReferenceSelfiePath).HasMaxLength(500);
            entity.Property(e => e.IdDocumentType).HasMaxLength(100);
            entity.Property(e => e.StatusReason).HasMaxLength(2000);

            // SECURITY: Encrypt extracted ID document PII
            entity.Property(e => e.IdNumber).HasMaxLength(500).HasConversion(nullablePiiConverter);
            entity.Property(e => e.FullName).HasMaxLength(500).HasConversion(nullablePiiConverter);
            entity.Property(e => e.DateOfBirth).HasMaxLength(500).HasConversion(nullablePiiConverter);

            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.DiditSessionId).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => new { e.UserId, e.CreatedAt }); // "latest verification for user" query
        });

        modelBuilder.Entity<CustomerVerification>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450); // FK to ApplicationUser.Id (string)
            entity.Property(e => e.DiditSessionId).HasMaxLength(64);
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.ReferenceSelfiePath).HasMaxLength(500);
            entity.Property(e => e.IdDocumentType).HasMaxLength(100);
            entity.Property(e => e.StatusReason).HasMaxLength(2000);

            // SECURITY: Encrypt extracted ID document PII (same converter as DriverVerification)
            entity.Property(e => e.IdNumber).HasMaxLength(500).HasConversion(nullablePiiConverter);
            entity.Property(e => e.FullName).HasMaxLength(500).HasConversion(nullablePiiConverter);
            entity.Property(e => e.DateOfBirth).HasMaxLength(500).HasConversion(nullablePiiConverter);

            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.DiditSessionId).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => new { e.UserId, e.CreatedAt }); // "latest verification for user" query
        });

        modelBuilder.Entity<ShiftFaceCheck>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.CreatedAt });
        });
    }
}
