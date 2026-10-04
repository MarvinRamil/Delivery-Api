using BeeLogistics.Modules.Drivers.Domain;
using Microsoft.EntityFrameworkCore;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Infrastructure;

public class DriversDbContext : DbContext
{
    private readonly IDataProtectorService? _dataProtector;

    public DriversDbContext(DbContextOptions<DriversDbContext> options, IDataProtectorService? dataProtector = null) : base(options)
    {
        _dataProtector = dataProtector;
    }

    public DbSet<DriverApplication> DriverApplications => Set<DriverApplication>();
    public DbSet<DriverWallet> DriverWallets => Set<DriverWallet>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<DriverTopUp> DriverTopUps => Set<DriverTopUp>();
    public DbSet<WithdrawalRequest> WithdrawalRequests => Set<WithdrawalRequest>();
    public DbSet<DriverMission> DriverMissions => Set<DriverMission>();
    public DbSet<GlobalMission> GlobalMissions => Set<GlobalMission>();
    public DbSet<SavedWithdrawalMethod> SavedWithdrawalMethods => Set<SavedWithdrawalMethod>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<DriverCashBondConfig> DriverCashBondConfigs => Set<DriverCashBondConfig>();
    public DbSet<DriverCashBondConfigVersion> DriverCashBondConfigVersions => Set<DriverCashBondConfigVersion>();
    public DbSet<DriverPackageInsuranceFeeConfig> DriverPackageInsuranceFeeConfigs => Set<DriverPackageInsuranceFeeConfig>();
    public DbSet<DriverPackageInsuranceFeeConfigVersion> DriverPackageInsuranceFeeConfigVersions => Set<DriverPackageInsuranceFeeConfigVersion>();
    public DbSet<DriverPackageInsurancePolicy> DriverPackageInsurancePolicies => Set<DriverPackageInsurancePolicy>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("drivers");

        // Value Converter for PII encryption
        var piiConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<string, string>(
            v => _dataProtector != null ? _dataProtector.Encrypt(v, "PII") : v,
            v => _dataProtector != null ? _dataProtector.Decrypt(v, "PII") : v);

        var nullablePiiConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<string?, string?>(
            v => v != null && _dataProtector != null ? _dataProtector.Encrypt(v, "PII") : v,
            v => v != null && _dataProtector != null ? _dataProtector.Decrypt(v, "PII") : v);

        modelBuilder.Entity<DriverApplication>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).HasMaxLength(450); // FK to ApplicationUser.Id (string)
            
            // SECURITY: Encrypt Driver PII
            entity.Property(e => e.FullName).IsRequired().HasMaxLength(500).HasConversion(piiConverter);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(500).HasConversion(piiConverter);
            entity.Property(e => e.Phone).IsRequired().HasMaxLength(500).HasConversion(piiConverter);

            // Blind index (deterministic HMAC) so encrypted Email stays searchable.
            // Populated by BlindIndexSaveChangesInterceptor.
            entity.Property<string>("EmailHash").HasMaxLength(64);
            entity.HasIndex("EmailHash");
            entity.Property(e => e.FacebookProfileUrl).HasMaxLength(500);

            entity.Property(e => e.VehicleType).HasMaxLength(50);
            entity.Property(e => e.VehiclePlate).HasMaxLength(50);
            entity.Property(e => e.VehicleModel).HasMaxLength(100);
            entity.Property(e => e.VehicleColor).HasMaxLength(50);

            entity.Property(e => e.DriversLicensePath).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ClearancePath).IsRequired().HasMaxLength(500);
            entity.Property(e => e.OrCrPath).IsRequired().HasMaxLength(500);
            entity.Property(e => e.LtfrbPaPath).IsRequired().HasMaxLength(500);
            entity.Property(e => e.InsurancePath).IsRequired().HasMaxLength(500);

            entity.Property(e => e.Notes).HasMaxLength(2000);

            // Existing rows predate resubmission tracking: they were submitted once.
            entity.Property(e => e.SubmissionCount).HasDefaultValue(1);

            entity.HasIndex(e => e.UserId); // Index for FK lookup
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => new { e.UserId, e.Status }); // Composite index for "my pending application" query
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<DriverWallet>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DriverId).IsRequired(); // FK to ApplicationUser.Id (Guid converted to string)
            entity.Property(e => e.Balance).HasPrecision(18, 2);
            entity.Property(e => e.TopUpBalance).HasPrecision(18, 2);
            entity.Property(e => e.PendingPayout).HasPrecision(18, 2);
            entity.Property(e => e.CashBondBalance).HasPrecision(18, 2);
            entity.Property(e => e.CashJobBlockThresholdOverride).HasPrecision(18, 2);
            entity.Property(e => e.TopUpNegativeLimitOverride).HasPrecision(18, 2);
            
            // SECURITY: Encrypt Bank PII
            entity.Property(e => e.BankAccountNumber).HasMaxLength(500).HasConversion(nullablePiiConverter);
            entity.Property(e => e.BankName).HasMaxLength(100);
            entity.Property(e => e.AccountHolderName).HasMaxLength(500).HasConversion(nullablePiiConverter);
            
            // PayMongo child account (issue #91). Ids are opaque provider strings, not PII, so
            // unlike the bank columns above they are stored in the clear - they are useless without
            // our secret key, and encrypting them would block the lookup index below.
            entity.Property(e => e.PayMongoAccountId).HasMaxLength(64);
            entity.Property(e => e.PayMongoWalletId).HasMaxLength(64);
            entity.Property(e => e.PayMongoAccountNumber).HasMaxLength(64);
            entity.Property(e => e.PayMongoLedgerAccountId).HasMaxLength(64);
            entity.Property(e => e.PayMongoAccountEmail).HasMaxLength(256);
            entity.Property(e => e.PayMongoVerificationFailureReason).HasMaxLength(512);
            entity.Property(e => e.PayMongoActivationStatus).HasConversion<int>();

            // Optimistic concurrency using PostgreSQL xmin (mapped to Version property)
            entity.Property(e => e.Version)
                .IsRowVersion()
                .HasColumnName("xmin");

            entity.HasIndex(e => e.DriverId).IsUnique();

            // One child account per wallet, enforced by the database rather than by the handler.
            // A duplicate would mean two drivers sharing one PayMongo balance, so this is a
            // money-safety constraint, not a tidiness one. Filtered: most wallets are unlinked.
            entity.HasIndex(e => e.PayMongoAccountId)
                .IsUnique()
                .HasFilter("\"PayMongoAccountId\" IS NOT NULL");
            
            entity.HasMany(e => e.Transactions)
                .WithOne(t => t.Wallet)
                .HasForeignKey(t => t.WalletId)
                .OnDelete(DeleteBehavior.Restrict);
            
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<WalletTransaction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Type).IsRequired();
            entity.Property(e => e.Bucket).IsRequired();
            entity.Property(e => e.Status).IsRequired();
            
            entity.HasIndex(e => e.WalletId);
            entity.HasIndex(e => e.TransactionDate);
            entity.HasIndex(e => e.RelatedBookingId);
            entity.HasIndex(e => e.RelatedWithdrawalRequestId);
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => e.Bucket);
            entity.HasIndex(e => new { e.WalletId, e.TransactionDate });

            entity.Property(e => e.ProviderPaymentId).HasMaxLength(128);

            // The database-level guard for provider-driven credits (GitLab #66).
            //
            // Top-up rows carry a null RelatedBookingId, so the booking-scoped unique index below
            // never covered them. Their only protection was HasTransactionByDescriptionAsync -
            // string equality on free text with the provider id embedded in prose. xmin on the
            // wallet stopped the concurrent case; nothing stopped the sequential one if the
            // wording ever drifted, and it had already drifted once on the cash-earning path.
            //
            // Filtered so the many rows with no provider payment stay unconstrained, and so a
            // soft-deleted row cannot block a legitimate replacement.
            //
            // A declined attempt and a later successful credit coexist under this key because
            // they store different ids, not different types - both are Type.TopUp. The credit
            // records the checkout the money arrived through (Xendit invoice / PayMongo
            // cs_...), the decline records the individual failed payment attempt (pay_...).
            // Two declines on one checkout are therefore two rows, which is correct; two
            // deliveries of one decline are one row, which is the point.
            entity.HasIndex(e => new { e.WalletId, e.Type, e.ProviderPaymentId })
                .IsUnique()
                .HasFilter("\"ProviderPaymentId\" IS NOT NULL AND \"IsDeleted\" = false");

            // Makes double-crediting structurally impossible instead of relying on a
            // check-then-act in the consumers: a redelivered message that slips past the
            // application guard (or two concurrent deliveries that both pass it) now fails on
            // the constraint rather than moving money twice. Matches the unique idempotency
            // indexes DriverTopUp and WithdrawalRequest already carry.
            //
            // Filtered to booking-linked rows: mission-reward earnings and manual adjustments
            // carry a null RelatedBookingId and must stay unconstrained.
            //
            // NOTE: this asserts at most one row per (wallet, booking, type), which holds today -
            // Earning, EarningReversal and CashSettlementDebit are each one-per-booking. If
            // multiple partial refunds per booking are ever supported (see #31), EarningReversal
            // will need a discriminator in this key.
            // Soft-deleted rows are excluded so a reversed/voided transaction does not block a
            // legitimate replacement for the same booking.
            entity.HasIndex(e => new { e.WalletId, e.RelatedBookingId, e.Type })
                .IsUnique()
                .HasFilter("\"RelatedBookingId\" IS NOT NULL AND \"IsDeleted\" = false");

            // Package-insurance claim-time race guard (issue #104): unlike ProviderPaymentId,
            // PolicyYearNumber is known and set on the initial insert, so this is a genuine
            // database-level guard against two concurrent claims for the same driver+year, not
            // just a check-then-act read. Status <> 2 (Failed) excludes dead rows so a retry
            // after a failed/expired QR isn't blocked by its own earlier attempt.
            entity.HasIndex(e => new { e.WalletId, e.Type, e.PolicyYearNumber })
                .IsUnique()
                .HasFilter("\"PolicyYearNumber\" IS NOT NULL AND \"Status\" <> 2 AND \"IsDeleted\" = false");

            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<DriverTopUp>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.ExternalId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.IdempotencyKey).HasMaxLength(100);
            
            // Optimistic concurrency using PostgreSQL xmin
            entity.Property(e => e.Version)
                .IsRowVersion()
                .HasColumnName("xmin");

            entity.Property(e => e.Provider).IsRequired().HasMaxLength(20).HasDefaultValue("xendit");
            entity.Property(e => e.ProviderPaymentId).HasMaxLength(128);
            entity.Property(e => e.ProviderCheckoutUrl).HasMaxLength(500);
            // TODO(provider-cleanup): frozen legacy columns, dropped in a follow-up migration
            entity.Property(e => e.XenditInvoiceId).HasMaxLength(128);
            entity.Property(e => e.XenditInvoiceUrl).HasMaxLength(500);
            entity.Property(e => e.FailureReason).HasMaxLength(500);
            entity.Property(e => e.Status).IsRequired();

            entity.HasIndex(e => e.DriverId);
            entity.HasIndex(e => e.WalletId);
            entity.HasIndex(e => e.ExternalId).IsUnique();
            entity.HasIndex(e => new { e.DriverId, e.IdempotencyKey }).IsUnique();
            entity.HasIndex(e => new { e.Provider, e.ProviderPaymentId }).IsUnique();
            entity.HasIndex(e => e.XenditInvoiceId).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<WithdrawalRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DriverId).IsRequired(); // FK to ApplicationUser.Id (Guid)
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            
            // SECURITY: Encrypt Bank PII in Requests
            entity.Property(e => e.BankAccountNumber).IsRequired().HasMaxLength(500).HasConversion(piiConverter);
            entity.Property(e => e.BankName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.AccountHolderName).IsRequired().HasMaxLength(500).HasConversion(piiConverter);
            
            entity.Property(e => e.RejectionReason).HasMaxLength(1000);
            entity.Property(e => e.IdempotencyKey).HasMaxLength(128);
            // Money column, so the same precision as Amount rather than a float.
            entity.Property(e => e.Fee).HasPrecision(18, 2);
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(20).HasDefaultValue("xendit");
            entity.Property(e => e.ProviderDisbursementId).HasMaxLength(128);
            // Existing rows are all bank transfers; QR Ph came later.
            entity.Property(e => e.DestinationType).IsRequired().HasDefaultValue(WithdrawalDestinationType.BankAccount);
            entity.Property(e => e.QrId).HasMaxLength(128);
            // TODO(provider-cleanup): frozen legacy column, dropped in a follow-up migration
            entity.Property(e => e.XenditDisbursementId).HasMaxLength(128);

            entity.HasIndex(e => e.DriverId);
            entity.HasIndex(e => e.WalletId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.RequestedAt);
            entity.HasIndex(e => new { e.DriverId, e.Status });
            entity.HasIndex(e => new { e.DriverId, e.IdempotencyKey }).IsUnique();
            entity.HasIndex(e => new { e.Provider, e.ProviderDisbursementId });
            
            entity.HasOne(e => e.Wallet)
                .WithMany()
                .HasForeignKey(e => e.WalletId)
                .OnDelete(DeleteBehavior.Restrict);
            
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<DriverMission>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.GlobalMissionId);
            entity.Property(e => e.DriverId).IsRequired(); // FK to ApplicationUser.Id (Guid)
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.Reward).HasPrecision(18, 2);
            entity.Property(e => e.Type).IsRequired();
            entity.Property(e => e.Status).IsRequired();
            
            entity.HasIndex(e => e.DriverId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => e.GlobalMissionId);
            entity.HasIndex(e => new { e.DriverId, e.Status });
            
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<GlobalMission>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.Reward).HasPrecision(18, 2);
            entity.Property(e => e.Type).IsRequired();
            entity.Property(e => e.Target).IsRequired();
            entity.Property(e => e.IsActive).IsRequired();
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => e.IsActive);

            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<SavedWithdrawalMethod>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DriverId).IsRequired();
            entity.Property(e => e.BankName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.BankCode).IsRequired().HasMaxLength(50);
            
            // SECURITY: Encrypt Bank PII
            entity.Property(e => e.AccountNumber).IsRequired().HasMaxLength(500).HasConversion(piiConverter);
            entity.Property(e => e.AccountHolderName).IsRequired().HasMaxLength(500).HasConversion(piiConverter);
            
            // Indexes for performance
            entity.HasIndex(e => e.DriverId);
            entity.HasIndex(e => new { e.DriverId, e.IsDefault });
            entity.HasIndex(e => new { e.DriverId, e.IsActive });
            
            // Query filter to exclude soft-deleted records
            entity.HasQueryFilter(e => !e.IsDeleted && e.IsActive);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Payload).IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.HasIndex(e => e.ProcessedAt); // poll: WHERE ProcessedAt IS NULL
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<DriverCashBondConfig>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.VehicleType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.HasIndex(e => e.VehicleType).IsUnique();
            entity.HasIndex(e => e.Version);
            entity.HasMany(e => e.Versions).WithOne(v => v.DriverCashBondConfig)
                .HasForeignKey(v => v.DriverCashBondConfigId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<DriverCashBondConfigVersion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.HasIndex(e => e.DriverCashBondConfigId);
            entity.HasIndex(e => new { e.DriverCashBondConfigId, e.Version });
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<DriverPackageInsuranceFeeConfig>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.VehicleType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.HasIndex(e => e.VehicleType).IsUnique();
            entity.HasIndex(e => e.Version);
            entity.HasMany(e => e.Versions).WithOne(v => v.DriverPackageInsuranceFeeConfig)
                .HasForeignKey(v => v.DriverPackageInsuranceFeeConfigId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<DriverPackageInsuranceFeeConfigVersion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.HasIndex(e => e.DriverPackageInsuranceFeeConfigId);
            entity.HasIndex(e => new { e.DriverPackageInsuranceFeeConfigId, e.Version });
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<DriverPackageInsurancePolicy>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DriverId).IsRequired();
            entity.Property(e => e.Status).IsRequired();

            // Optimistic concurrency using PostgreSQL xmin - mirrors DriverWallet above. This is
            // what makes two concurrent settlements of the same policy year fail safely instead
            // of both silently succeeding (WalletTransaction carries no row-version of its own).
            entity.Property(e => e.Version)
                .IsRowVersion()
                .HasColumnName("xmin");

            entity.HasIndex(e => e.DriverId).IsUnique();
            entity.HasQueryFilter(e => !e.IsDeleted);
        });
    }
}

