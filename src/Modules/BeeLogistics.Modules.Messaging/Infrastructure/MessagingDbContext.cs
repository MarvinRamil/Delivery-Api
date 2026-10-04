using BeeLogistics.Modules.Messaging.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Messaging.Infrastructure;

/// <summary>
/// Own schema, own migration history — one schema per module, as every other module here does.
/// </summary>
public class MessagingDbContext : DbContext
{
    public const string Schema = "messaging";

    public MessagingDbContext(DbContextOptions<MessagingDbContext> options) : base(options) { }

    public DbSet<MatrixIdentity> MatrixIdentities => Set<MatrixIdentity>();
    public DbSet<MatrixDevice> MatrixDevices => Set<MatrixDevice>();
    public DbSet<BookingRoom> BookingRooms => Set<BookingRoom>();
    public DbSet<RoomEvent> RoomEvents => Set<RoomEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<MatrixIdentity>(e =>
        {
            e.ToTable("matrix_identities");
            e.HasKey(x => x.Id);
            e.Property(x => x.BeeUserId).IsRequired().HasMaxLength(128);
            e.Property(x => x.MatrixUserId).IsRequired().HasMaxLength(255);
            // Both directions must be unique: one Matrix account per bee user, and no two bee
            // users sharing an MXID. The second is what would let one person read another's rooms.
            e.HasIndex(x => x.BeeUserId).IsUnique();
            e.HasIndex(x => x.MatrixUserId).IsUnique();
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<MatrixDevice>(e =>
        {
            e.ToTable("matrix_devices");
            e.HasKey(x => x.Id);
            e.Property(x => x.BeeUserId).IsRequired().HasMaxLength(128);
            e.Property(x => x.Platform).IsRequired().HasMaxLength(64);
            e.Property(x => x.DeviceId).IsRequired().HasMaxLength(255);
            // One device per (user, platform) — a re-login reuses it instead of accumulating one
            // device per app launch.
            e.HasIndex(x => new { x.BeeUserId, x.Platform }).IsUnique();
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<BookingRoom>(e =>
        {
            e.ToTable("booking_rooms");
            e.HasKey(x => x.Id);
            e.Property(x => x.BookingNumber).IsRequired().HasMaxLength(100);
            e.Property(x => x.RoomId).IsRequired().HasMaxLength(255);
            e.Property(x => x.RoomAlias).IsRequired().HasMaxLength(255);
            e.Property(x => x.DriverMatrixUserId).HasMaxLength(255);
            e.Property(x => x.CustomerMatrixUserId).HasMaxLength(255);
            e.Property(x => x.State).HasConversion<int>();
            // THE idempotency guarantee for room provisioning: a redelivered BookingChatRoomRequested
            // cannot create a second room for the same booking.
            e.HasIndex(x => x.BookingId).IsUnique();
            e.HasIndex(x => x.RoomId).IsUnique();
            e.HasIndex(x => x.BookingNumber);
            // The two retention sweeps scan by state and age; one index each so neither
            // degrades into a table scan as the archive grows.
            e.HasIndex(x => new { x.State, x.EndedAt });
            e.HasIndex(x => new { x.State, x.FrozenAt });
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<RoomEvent>(e =>
        {
            e.ToTable("room_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.MatrixEventId).IsRequired().HasMaxLength(255);
            e.Property(x => x.RoomId).IsRequired().HasMaxLength(255);
            e.Property(x => x.Sender).IsRequired().HasMaxLength(255);
            e.Property(x => x.EventType).IsRequired().HasMaxLength(128);
            e.Property(x => x.Body);
            e.Property(x => x.Content).IsRequired().HasColumnType("jsonb");
            // Synapse retries a transaction until it gets a 2xx, so the same event really does
            // arrive twice. This index is what lets the endpoint answer 200 unconditionally.
            e.HasIndex(x => x.MatrixEventId).IsUnique();
            // The back-office transcript query: one booking, in order.
            e.HasIndex(x => new { x.BookingId, x.OriginServerTs });
            e.HasIndex(x => new { x.RoomId, x.OriginServerTs });
            // Archive rows are evidence for disputes; nothing soft-deletes them, and no query
            // filter is applied so a deleted-by-accident row would still be visible to an audit.
        });
    }
}
