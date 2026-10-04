using Microsoft.EntityFrameworkCore;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Payment.Infrastructure;

/// <summary>Data-protection purposes used by this context.</summary>
public static class DataProtectionPurposes
{
    /// <summary>
    /// Purpose for provider payment tokens (customer ids + payment method ids).
    /// The value is the historical literal "XenditToken": changing it would derive a
    /// different subkey and make every stored token undecryptable. It intentionally
    /// covers ALL providers — purpose separation matters across data classes
    /// (PII vs tokens), not across vendors of the same data class.
    /// </summary>
    public const string PaymentProviderToken = "XenditToken";
}

public class PaymentDbContext : DbContext
{
    private readonly IDataProtectorService? _dataProtector;

    public PaymentDbContext(DbContextOptions<PaymentDbContext> options, IDataProtectorService? dataProtector = null) : base(options)
    {
        _dataProtector = dataProtector;
    }

    public DbSet<Domain.Payment> Payments { get; set; }
    public DbSet<Domain.SavedPaymentMethod> SavedPaymentMethods { get; set; }
    public DbSet<Domain.PaymentWebhookEvent> WebhookEvents { get; set; }

    // Payment's own transactional outbox (not MassTransit's built-in one - see OutboxMessage's
    // doc comment for why). Payment used to publish exclusively through IBus, which delivers
    // straight to the broker and keeps nothing: a RabbitMQ outage lost the message with no record
    // it was ever meant to be sent. A message staged here in a scope is committed in the same
    // transaction as the business data and dispatched by PaymentOutboxDispatcherService.
    public DbSet<OutboxMessage> OutboxMessages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("payment");

        // Value Converter for encryption (nullable variant passes null through)
        var providerTokenConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<string, string>(
            v => _dataProtector != null ? _dataProtector.Encrypt(v, DataProtectionPurposes.PaymentProviderToken) : v,
            v => _dataProtector != null ? _dataProtector.Decrypt(v, DataProtectionPurposes.PaymentProviderToken) : v);

        modelBuilder.Entity<Domain.Payment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PaymentNumber).IsRequired().HasMaxLength(50);
            entity.Property(e => e.BookingId).IsRequired(false); // Nullable for PayOnline flow where payment is created before booking
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.ExternalId).HasMaxLength(100);
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(20).HasDefaultValue("xendit");
            entity.Property(e => e.ProviderPaymentId).HasMaxLength(100);
            entity.Property(e => e.ProviderCheckoutUrl).HasMaxLength(500);
            entity.Property(e => e.ProviderCaptureId).HasMaxLength(100);
            entity.Property(e => e.ProviderRefundId).HasMaxLength(100);
            // TODO(provider-cleanup): frozen legacy columns, dropped in a follow-up migration
            entity.Property(e => e.XenditInvoiceId).HasMaxLength(100);
            entity.Property(e => e.XenditInvoiceUrl).HasMaxLength(500);
            entity.Property(e => e.XenditPaymentRequestId).HasMaxLength(100);
            entity.Property(e => e.XenditRefundId).HasMaxLength(100);
            entity.Property(e => e.RefundAmount).HasPrecision(18, 2);
            entity.Property(e => e.TotalRefunded).HasPrecision(18, 2);
            // Computed from Amount and TotalRefunded; never a column.
            entity.Ignore(e => e.RefundableAmount);
            entity.Property(e => e.FailureReason).HasMaxLength(500);
            entity.Property(e => e.IdempotencyKey).HasMaxLength(128);

            // Optimistic concurrency using PostgreSQL xmin, mirroring Booking, DriverWallet and
            // DriverTopUp. xmin is a system column, so this adds no storage and no table rewrite.
            entity.Property(e => e.Version)
                .IsRowVersion()
                .HasColumnName("xmin");

            entity.HasIndex(e => e.PaymentNumber).IsUnique();
            entity.HasIndex(e => new { e.Provider, e.ProviderPaymentId });
            entity.HasIndex(e => new { e.Provider, e.ProviderRefundId });
            entity.HasIndex(e => e.XenditInvoiceId);
            entity.HasIndex(e => e.XenditRefundId);
            entity.HasIndex(e => e.BookingId);

            // Makes a client double-submit return the first payment instead of opening a second
            // real checkout. Filtered so the many rows without a key do not collide.
            entity.HasIndex(e => new { e.CustomerId, e.IdempotencyKey })
                .IsUnique()
                .HasFilter("\"IdempotencyKey\" IS NOT NULL");

            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<Domain.PaymentWebhookEvent>(entity =>
        {
            entity.ToTable("WebhookEvents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(50);
            entity.Property(e => e.EventKey).IsRequired().HasMaxLength(200);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Payload).IsRequired();
            entity.Property(e => e.Error).HasMaxLength(2000);
            // Idempotency: a provider delivery is recorded exactly once
            entity.HasIndex(e => new { e.Provider, e.EventKey }).IsUnique();
            entity.HasIndex(e => e.ReceivedAt);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<Domain.SavedPaymentMethod>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CustomerId).IsRequired();
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(20).HasDefaultValue("xendit");

            // SECURITY: Field-level encryption for sensitive tokens.
            // Same converter (and therefore the same purpose-derived key) as the
            // legacy Xendit columns, so the migration backfill can copy ciphertext.
            entity.Property(e => e.ProviderCustomerId)
                .IsRequired()
                .HasMaxLength(500) // Increased for encryption overhead
                .HasConversion(providerTokenConverter);

            entity.Property(e => e.ProviderPaymentMethodId)
                .IsRequired()
                .HasMaxLength(500) // Increased for encryption overhead
                .HasConversion(providerTokenConverter);

            // TODO(provider-cleanup): frozen legacy columns, dropped in a follow-up migration
            entity.Property(e => e.XenditCustomerId)
                .HasMaxLength(500)
                .HasConversion(providerTokenConverter!);

            entity.Property(e => e.XenditPaymentMethodId)
                .HasMaxLength(500)
                .HasConversion(providerTokenConverter!);

            entity.Property(e => e.Type).IsRequired();
            entity.Property(e => e.Last4Digits).IsRequired().HasMaxLength(4);
            entity.Property(e => e.CardBrand).HasMaxLength(50);
            entity.Property(e => e.CardholderName).HasMaxLength(200);

            // Indexes for performance and uniqueness
            entity.HasIndex(e => e.CustomerId);
            // Note: Cannot have unique index on encrypted column if search is needed.
            // But since these are tokens, we only use them for retrieval by CustomerId.
            entity.HasIndex(e => e.XenditCustomerId);
            
            entity.HasIndex(e => new { e.CustomerId, e.IsDefault });
            entity.HasIndex(e => new { e.CustomerId, e.IsActive });
            
            // Query filter to exclude soft-deleted records
            entity.HasQueryFilter(e => !e.IsDeleted && e.IsActive);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages", "payment");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.ProcessedAt);
        });
    }

    public override int SaveChanges() => base.SaveChanges();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => base.SaveChangesAsync(cancellationToken);
}
