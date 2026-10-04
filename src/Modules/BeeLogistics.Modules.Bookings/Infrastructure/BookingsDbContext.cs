using BeeLogistics.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using BeeLogistics.Modules.Bookings.Domain;
using MassTransit.EntityFrameworkCoreIntegration;

namespace BeeLogistics.Modules.Bookings.Infrastructure;

public class BookingsDbContext : DbContext
{
    public BookingsDbContext(DbContextOptions<BookingsDbContext> options) : base(options)
    {
    }

    public DbSet<Booking> Bookings { get; set; }
    public DbSet<Customer> Customers { get; set; }
    public DbSet<DriverBookingOffer> DriverBookingOffers { get; set; }
    public DbSet<DeliveryStop> DeliveryStops { get; set; }
    public DbSet<ProofOfDelivery> ProofOfDeliveries { get; set; }
    public DbSet<FavouriteDriver> FavouriteDrivers { get; set; }
    public DbSet<Tip> Tips { get; set; }
    public DbSet<VehiclePricing> VehiclePricings { get; set; }
    public DbSet<VehiclePricingVersion> VehiclePricingVersions { get; set; }
    
    // MassTransit Outbox/Inbox entities
    public DbSet<OutboxState> OutboxState { get; set; }
    public DbSet<OutboxMessage> OutboxMessage { get; set; }
    public DbSet<InboxState> InboxState { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("sales");

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
            entity.Property(e => e.PasswordHash).HasMaxLength(500);
            entity.Property(e => e.CompanyName).HasMaxLength(255);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.Address).HasMaxLength(500);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.HasMany(e => e.Bookings)
                .WithOne(b => b.Customer)
                .HasForeignKey(b => b.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<Booking>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.BookingNumber).IsRequired().HasMaxLength(100);
            entity.Property(e => e.PickupLocation).HasMaxLength(500);
            entity.Property(e => e.DropoffLocation).HasMaxLength(500);
            entity.Property(e => e.VehicleType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.CargoDescription).HasMaxLength(1000);
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.ServiceType).HasConversion<string>().HasMaxLength(20);
            // The default must be spelled out: without it EF backfills existing rows with an
            // empty string, which is not a valid enum name and fails on read.
            entity.Property(e => e.DeliveryMode).HasConversion<string>().HasMaxLength(20)
                .HasDefaultValue(DeliveryMode.Regular);
            // Default matches DeliveryModePolicy.RegularDispatchPriority. It must match the
            // migration's defaultValue, or the model snapshot drifts from the schema.
            entity.Property(e => e.DispatchPriority).HasDefaultValue(50);
            entity.Property(e => e.EstimatedFare).HasPrecision(10, 2);
            entity.Property(e => e.FinalFare).HasPrecision(10, 2);
            entity.Property(e => e.DistanceKm).HasPrecision(10, 2);
            entity.Property(e => e.PriorityFee).HasPrecision(10, 2);
            entity.Property(e => e.ScheduledPickupWindow).HasMaxLength(50);
            entity.Property(e => e.Size).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.AssignmentStatus).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.WeightKg).HasPrecision(10, 2);
            entity.Property(e => e.ItemLengthCm).HasPrecision(10, 2);
            entity.Property(e => e.ItemWidthCm).HasPrecision(10, 2);
            entity.Property(e => e.ItemHeightCm).HasPrecision(10, 2);
            entity.Property(e => e.PickupLatitude).HasPrecision(18, 8);
            entity.Property(e => e.PickupLongitude).HasPrecision(18, 8);
            entity.Property(e => e.DropoffLatitude).HasPrecision(18, 8);
            entity.Property(e => e.DropoffLongitude).HasPrecision(18, 8);
            entity.Property(e => e.CancellationReason).HasMaxLength(500);
            entity.Property(e => e.CancelledBy);
            entity.Property(e => e.CancelledAt);
            entity.Property(e => e.NextPulseAt);
            entity.Property(e => e.PulseCount).HasDefaultValue(0);
            entity.HasMany(e => e.Stops).WithOne(s => s.Booking).HasForeignKey(s => s.BookingId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.ProofOfDeliveries).WithOne(p => p.Booking).HasForeignKey(p => p.BookingId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.SelectedDriverId);
            entity.Property(e => e.FavouriteDriverId);
            entity.HasIndex(e => e.BookingNumber).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ServiceType);
            entity.HasIndex(e => e.SelectedDriverId);
            entity.HasIndex(e => e.FavouriteDriverId);
            entity.HasIndex(e => e.CustomerId);
            entity.HasIndex(e => e.ScheduledDateTime);
            entity.HasIndex(e => e.AssignmentStatus);
            // Partial index scoped to the one status the pulse jobs actually query by
            // NextPulseAt for: smaller and cheaper to maintain than a full composite
            // index, since only a tiny, fast-changing slice of bookings is ever
            // BroadcastingToDrivers at once.
            entity.HasIndex(e => e.NextPulseAt)
                .HasFilter("\"AssignmentStatus\" = 'BroadcastingToDrivers'");

            entity.HasIndex(e => e.DeliveryMode);

            // Cross-booking dispatch order for the due-only pulse query. Same partial filter as
            // the index above, and it must stay byte-identical: this is raw SQL comparing against
            // the enum *name*, which only works because AssignmentStatus is HasConversion<string>().
            // NextPulseAt is second here, so this does not subsume the single-column index above.
            entity.HasIndex(e => new { e.DispatchPriority, e.NextPulseAt })
                .IsDescending(true, false)
                .HasFilter("\"AssignmentStatus\" = 'BroadcastingToDrivers'");
            entity.HasQueryFilter(e => !e.IsDeleted);

            // Optimistic concurrency using PostgreSQL xmin (same pattern as DriverWallet).
            // Guards the driver-accept race so a booking can only be claimed once.
            entity.Property(e => e.Version)
                .IsRowVersion()
                .HasColumnName("xmin");
        });
        
        modelBuilder.Entity<DeliveryStop>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Address).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ContactName).HasMaxLength(255);
            entity.Property(e => e.ContactPhone).HasMaxLength(50);
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Latitude).HasPrecision(18, 8);
            entity.Property(e => e.Longitude).HasPrecision(18, 8);
            entity.HasIndex(e => e.BookingId);
            entity.HasIndex(e => new { e.BookingId, e.Sequence });
            entity.HasIndex(e => e.Status);
        });
        
        modelBuilder.Entity<ProofOfDelivery>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ImagePath).HasMaxLength(500);
            entity.Property(e => e.SignaturePath).HasMaxLength(500);
            entity.Property(e => e.RecipientName).HasMaxLength(255);
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.HasOne(e => e.Stop).WithMany().HasForeignKey(e => e.StopId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.BookingId);
            entity.HasIndex(e => e.StopId);
        });
        
        modelBuilder.Entity<FavouriteDriver>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CustomerId).IsRequired();
            entity.Property(e => e.DriverId).IsRequired();
            entity.HasIndex(e => new { e.CustomerId, e.DriverId }).IsUnique();
            entity.HasIndex(e => e.CustomerId);
            entity.HasIndex(e => e.DriverId);
        });
        
        modelBuilder.Entity<Tip>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CustomerId).IsRequired();
            entity.Property(e => e.DriverId).IsRequired();
            entity.Property(e => e.Amount).HasPrecision(10, 2);
            entity.Property(e => e.Message).HasMaxLength(500);
            entity.HasOne(e => e.Booking).WithMany().HasForeignKey(e => e.BookingId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.BookingId).IsUnique();
            entity.HasIndex(e => e.DriverId);
            entity.HasIndex(e => e.CustomerId);
            entity.HasIndex(e => e.TippedAt);
        });

        modelBuilder.Entity<DriverBookingOffer>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DriverId).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.DistanceKm).HasPrecision(10, 2);
            entity.Property(e => e.DriverRating).HasPrecision(3, 2);
            entity.HasIndex(e => e.BookingId);
            entity.HasIndex(e => e.DriverId);
            // One offer per driver per booking: the pulse job and broadcast consumer
            // can race on offer creation; this makes the business rule a DB invariant.
            entity.HasIndex(e => new { e.BookingId, e.DriverId }).IsUnique();
            entity.HasIndex(e => new { e.DriverId, e.Status });
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => e.IsFavouriteDriver);
            entity.HasIndex(e => new { e.BookingId, e.SequenceNumber });
            entity.HasOne<Booking>().WithMany().HasForeignKey(e => e.BookingId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OutboxState>(entity => { entity.ToTable("OutboxState", "sales"); entity.HasKey(e => e.OutboxId); });
        modelBuilder.Entity<OutboxMessage>(entity => { entity.ToTable("OutboxMessage", "sales"); entity.HasKey(e => e.SequenceNumber); entity.HasIndex(e => e.EnqueueTime); entity.HasIndex(e => e.ExpirationTime); });
        modelBuilder.Entity<InboxState>(entity => { entity.ToTable("InboxState", "sales"); entity.HasKey(e => e.Id); entity.HasIndex(e => new { e.MessageId, e.ConsumerId }).IsUnique(); });

        modelBuilder.Entity<VehiclePricing>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.VehicleType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Types).HasMaxLength(200);
            entity.Property(e => e.SizeLimit).HasMaxLength(100);
            entity.Property(e => e.SurchargeInfo).HasMaxLength(1000);
            entity.Property(e => e.Remarks).HasMaxLength(2000);
            entity.Property(e => e.UpdatedByUserName).HasMaxLength(255);
            entity.Property(e => e.BaseFare).HasPrecision(10, 2);
            entity.Property(e => e.PerKm0to5).HasPrecision(10, 2);
            entity.Property(e => e.PerKmAbove5).HasPrecision(10, 2);
            entity.Property(e => e.AdditionalStopFee).HasPrecision(10, 2);
            entity.Property(e => e.WeightLimitKg).HasPrecision(10, 2);
            entity.Property(e => e.WeightSurchargePerKg).HasPrecision(10, 2);
            entity.Property(e => e.LongDistanceBaseFare).HasPrecision(10, 2);
            entity.Property(e => e.LongDistancePerKm41to60).HasPrecision(10, 2);
            entity.Property(e => e.LongDistancePerKmAbove60).HasPrecision(10, 2);
            entity.HasIndex(e => e.VehicleType).IsUnique();
            entity.HasIndex(e => e.IsActive);
            entity.HasIndex(e => e.Version);
            entity.HasMany(e => e.Versions).WithOne(v => v.VehiclePricing).HasForeignKey(v => v.VehiclePricingId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<VehiclePricingVersion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Types).HasMaxLength(200);
            entity.Property(e => e.SizeLimit).HasMaxLength(100);
            entity.Property(e => e.SurchargeInfo).HasMaxLength(1000);
            entity.Property(e => e.Remarks).HasMaxLength(2000);
            entity.Property(e => e.ChangedByUserName).HasMaxLength(255);
            entity.Property(e => e.BaseFare).HasPrecision(10, 2);
            entity.Property(e => e.PerKm0to5).HasPrecision(10, 2);
            entity.Property(e => e.PerKmAbove5).HasPrecision(10, 2);
            entity.Property(e => e.AdditionalStopFee).HasPrecision(10, 2);
            entity.Property(e => e.WeightLimitKg).HasPrecision(10, 2);
            entity.Property(e => e.WeightSurchargePerKg).HasPrecision(10, 2);
            entity.Property(e => e.LongDistanceBaseFare).HasPrecision(10, 2);
            entity.Property(e => e.LongDistancePerKm41to60).HasPrecision(10, 2);
            entity.Property(e => e.LongDistancePerKmAbove60).HasPrecision(10, 2);
            entity.HasIndex(e => e.VehiclePricingId);
            entity.HasIndex(e => new { e.VehiclePricingId, e.Version });
            entity.HasIndex(e => e.CreatedAt);
        });
    }
}
