using BeeLogistics.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Identity.Infrastructure;

public class IdentityAppDbContext : IdentityDbContext<ApplicationUser>
{
    private readonly IDataProtectorService? _dataProtector;

    public IdentityAppDbContext(DbContextOptions<IdentityAppDbContext> options, IDataProtectorService? dataProtector = null) : base(options)
    {
        _dataProtector = dataProtector;
    }

    public DbSet<RefreshToken> RefreshTokens { get; set; }
    public DbSet<AuditLog> AuditLogs { get; set; }
    public DbSet<Vehicle> Vehicles { get; set; }
    public DbSet<DriverVehicleAssignment> DriverVehicleAssignments { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("identity");

        // Value Converter for PII encryption. The encryption service is self-guarding: Encrypt
        // no-ops on already-encrypted input and Decrypt no-ops on non-encrypted input.
        var piiConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<string, string>(
            v => _dataProtector != null ? _dataProtector.Encrypt(v, "PII") : v,
            v => _dataProtector != null ? _dataProtector.Decrypt(v, "PII") : v);

        var nullablePiiConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<string?, string?>(
            v => v != null && _dataProtector != null ? _dataProtector.Encrypt(v, "PII") : v,
            v => v != null && _dataProtector != null ? _dataProtector.Decrypt(v, "PII") : v);

        // Rename Identity tables to be cleaner
        builder.Entity<ApplicationUser>().ToTable("Users");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityRole>().ToTable("Roles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserRole<string>>().ToTable("UserRoles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<string>>().ToTable("UserClaims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<string>>().ToTable("UserLogins");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityRoleClaim<string>>().ToTable("RoleClaims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<string>>().ToTable("UserTokens");

        // ApplicationUser configuration
        builder.Entity<ApplicationUser>(entity =>
        {
            // SECURITY: Encrypt PII fields
            entity.Property(e => e.FullName)
                .IsRequired()
                .HasMaxLength(500)
                .HasConversion(piiConverter);

            entity.Property(e => e.PhoneNumber)
                .HasMaxLength(500)
                .HasConversion(nullablePiiConverter);

            // Blind index (deterministic HMAC) so encrypted PhoneNumber stays searchable.
            // Populated by BlindIndexSaveChangesInterceptor.
            entity.Property<string>("PhoneNumberHash").HasMaxLength(64);
            entity.HasIndex("PhoneNumberHash");

            entity.Property(e => e.CurrentLatitude).HasPrecision(18, 8);
            entity.Property(e => e.CurrentLongitude).HasPrecision(18, 8);
            
            // Onboarding fields
            entity.Property(e => e.IsOnboarded).HasDefaultValue(false);
            entity.Property(e => e.IsOnline).HasDefaultValue(false);
            
            // Security question fields (up to 3 questions per user)
            entity.Property(e => e.SecurityAnswerHash1).HasMaxLength(256);
            entity.Property(e => e.SecurityAnswerHash2).HasMaxLength(256);
            entity.Property(e => e.SecurityAnswerHash3).HasMaxLength(256);
            
            // Profile picture URL (stored in S3)
            entity.Property(e => e.ProfilePictureUrl).HasMaxLength(500);

            // Face liveness verification
            entity.Property(e => e.LivenessVerifiedAt);

            // Driver vehicle info
            entity.Property(e => e.VehiclePlate).HasMaxLength(20);
            entity.Property(e => e.VehicleModel).HasMaxLength(100);
            entity.Property(e => e.VehicleColor).HasMaxLength(50);
            entity.Property(e => e.VehicleType).HasMaxLength(50);

            // Clerk identity link (nullable during migration; unique when set)
            entity.Property(e => e.ClerkUserId).HasMaxLength(255);
            entity.HasIndex(e => e.ClerkUserId)
                .IsUnique()
                .HasFilter("\"ClerkUserId\" IS NOT NULL");

            entity.HasMany(e => e.DriverVehicleAssignments)
                .WithOne(e => e.Driver)
                .HasForeignKey(e => e.DriverId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes
            entity.HasIndex(e => e.IsOnboarded);
            entity.HasIndex(e => e.IsOnline);
        });

        builder.Entity<Vehicle>(entity =>
        {
            entity.ToTable("Vehicles");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PlateNumber).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.Color).HasMaxLength(50);
            entity.Property(e => e.Type).HasMaxLength(50);
            entity.HasIndex(e => e.PlateNumber).IsUnique();
        });

        builder.Entity<DriverVehicleAssignment>(entity =>
        {
            entity.ToTable("DriverVehicleAssignments");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.DriverId, e.VehicleId }).IsUnique();
            entity.HasIndex(e => e.DriverId);
            entity.HasIndex(e => e.VehicleId);
            entity.HasIndex(e => new { e.DriverId, e.IsPrimary });

            entity.HasOne(e => e.Vehicle)
                .WithMany(e => e.DriverVehicleAssignments)
                .HasForeignKey(e => e.VehicleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // RefreshToken configuration
        builder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Token).IsRequired().HasMaxLength(500);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.DeviceId).HasMaxLength(256);
            entity.Property(e => e.DeviceFingerprint).HasMaxLength(512);
            entity.HasIndex(e => e.Token).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.DeviceId);
        });

        // AuditLog configuration
        builder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Action).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Category).IsRequired().HasMaxLength(50);
            entity.Property(e => e.UserId).HasMaxLength(450);
            entity.Property(e => e.UserName).HasMaxLength(256);
            entity.Property(e => e.UserEmail).HasMaxLength(256);
            entity.Property(e => e.UserRole).HasMaxLength(50);
            entity.Property(e => e.EntityId).HasMaxLength(450);
            entity.Property(e => e.EntityType).HasMaxLength(100);
            entity.Property(e => e.IpAddress).HasMaxLength(45);
            entity.Property(e => e.UserAgent).HasMaxLength(500);
            entity.Property(e => e.RequestId).HasMaxLength(50);
            entity.Property(e => e.ErrorMessage).HasMaxLength(1000);
            
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => e.IsArchived);
            entity.HasIndex(e => e.RequestId);
        });
    }
}
