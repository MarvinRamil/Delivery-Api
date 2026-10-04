using BeeLogistics.Modules.Drivers.Application.DTOs;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Drivers.Infrastructure;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Shared.Abstractions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Drivers.Application.Handlers;

// ── Admin config CRUD (mirrors DriverCashBondConfig's per-vehicle-type + versioning shape) ────

public record CreateOrUpdateDriverPackageInsuranceFeeConfigCommand(
    string VehicleType, decimal Amount, Guid? UpdatedByUserId, string? UpdatedByUserName)
    : IRequest<Result<DriverPackageInsuranceFeeConfigDto>>;

public record GetDriverPackageInsuranceFeeConfigsQuery : IRequest<Result<IReadOnlyList<DriverPackageInsuranceFeeConfigDto>>>;

public record GetDriverPackageInsuranceFeeConfigVersionsQuery(Guid DriverPackageInsuranceFeeConfigId)
    : IRequest<Result<IReadOnlyList<DriverPackageInsuranceFeeConfigVersionDto>>>;

// ── Driver-facing status + payment ──────────────────────────────────────────────────────────

public record GetDriverPackageInsuranceStatusQuery(Guid DriverId) : IRequest<Result<DriverPackageInsuranceStatusDto>>;

/// <summary>Issues (or re-issues) the QR that pays this driver's next-due package-insurance premium.</summary>
public record CreateInsurancePremiumQrCommand(Guid DriverId) : IRequest<Result<PackageInsuranceQrDto>>;

/// <summary>
/// Settles a package-insurance premium after its QR was paid, driven by the parent account's
/// <c>qr.paid</c> webhook.
/// </summary>
public record SettleInsurancePremiumQrPaymentCommand(string ReferenceLabel, decimal Amount, string IdempotencyKey)
    : IRequest<Result>;

internal static class DriverPackageInsuranceMapper
{
    public static DriverPackageInsuranceFeeConfigDto ToDto(DriverPackageInsuranceFeeConfig config) => new(
        config.Id, config.VehicleType, config.Amount, config.Version, config.CreatedAt, config.UpdatedAt);

    public static DriverPackageInsuranceFeeConfigVersionDto ToDto(DriverPackageInsuranceFeeConfigVersion version) => new(
        version.Id, version.Version, version.Amount, version.ChangedByUserId, version.ChangedByUserName, version.CreatedAt);
}

public class CreateOrUpdateDriverPackageInsuranceFeeConfigCommandHandler
    : IRequestHandler<CreateOrUpdateDriverPackageInsuranceFeeConfigCommand, Result<DriverPackageInsuranceFeeConfigDto>>
{
    private readonly IDriverPackageInsuranceFeeConfigRepository _repository;
    private readonly DriversDbContext _context;

    public CreateOrUpdateDriverPackageInsuranceFeeConfigCommandHandler(IDriverPackageInsuranceFeeConfigRepository repository, DriversDbContext context)
    {
        _repository = repository;
        _context = context;
    }

    public async Task<Result<DriverPackageInsuranceFeeConfigDto>> Handle(CreateOrUpdateDriverPackageInsuranceFeeConfigCommand request, CancellationToken ct)
    {
        if (request.Amount <= 0)
            return Result.Fail<DriverPackageInsuranceFeeConfigDto>("Amount must be positive");

        var existing = await _repository.GetByVehicleTypeAsync(request.VehicleType, ct);

        if (existing is null)
        {
            var config = new DriverPackageInsuranceFeeConfig(request.VehicleType, request.Amount, request.UpdatedByUserId, request.UpdatedByUserName);

            var initialVersion = new DriverPackageInsuranceFeeConfigVersion(
                config.Id, config.Version, config.Amount, request.UpdatedByUserId, request.UpdatedByUserName);

            _context.Set<DriverPackageInsuranceFeeConfigVersion>().Add(initialVersion);
            _repository.Add(config);
            await _repository.SaveChangesAsync(ct);

            return Result.Ok(DriverPackageInsuranceMapper.ToDto(config));
        }

        existing.UpdateAmount(request.Amount, request.UpdatedByUserId, request.UpdatedByUserName);

        var newVersion = new DriverPackageInsuranceFeeConfigVersion(
            existing.Id, existing.Version, existing.Amount, request.UpdatedByUserId, request.UpdatedByUserName);

        _context.Set<DriverPackageInsuranceFeeConfigVersion>().Add(newVersion);
        _repository.Update(existing);
        await _repository.SaveChangesAsync(ct);

        return Result.Ok(DriverPackageInsuranceMapper.ToDto(existing));
    }
}

public class GetDriverPackageInsuranceFeeConfigsQueryHandler : IRequestHandler<GetDriverPackageInsuranceFeeConfigsQuery, Result<IReadOnlyList<DriverPackageInsuranceFeeConfigDto>>>
{
    private readonly IDriverPackageInsuranceFeeConfigRepository _repository;

    public GetDriverPackageInsuranceFeeConfigsQueryHandler(IDriverPackageInsuranceFeeConfigRepository repository) => _repository = repository;

    public async Task<Result<IReadOnlyList<DriverPackageInsuranceFeeConfigDto>>> Handle(GetDriverPackageInsuranceFeeConfigsQuery request, CancellationToken ct)
    {
        var configs = await _repository.GetAllAsync(ct);
        return Result.Ok<IReadOnlyList<DriverPackageInsuranceFeeConfigDto>>(
            configs.OrderBy(c => c.VehicleType).Select(DriverPackageInsuranceMapper.ToDto).ToList());
    }
}

public class GetDriverPackageInsuranceFeeConfigVersionsQueryHandler
    : IRequestHandler<GetDriverPackageInsuranceFeeConfigVersionsQuery, Result<IReadOnlyList<DriverPackageInsuranceFeeConfigVersionDto>>>
{
    private readonly IDriverPackageInsuranceFeeConfigRepository _repository;

    public GetDriverPackageInsuranceFeeConfigVersionsQueryHandler(IDriverPackageInsuranceFeeConfigRepository repository) => _repository = repository;

    public async Task<Result<IReadOnlyList<DriverPackageInsuranceFeeConfigVersionDto>>> Handle(GetDriverPackageInsuranceFeeConfigVersionsQuery request, CancellationToken ct)
    {
        var versions = await _repository.GetVersionsByConfigIdAsync(request.DriverPackageInsuranceFeeConfigId, ct);
        return Result.Ok<IReadOnlyList<DriverPackageInsuranceFeeConfigVersionDto>>(
            versions.Select(DriverPackageInsuranceMapper.ToDto).ToList());
    }
}

public class GetDriverPackageInsuranceStatusQueryHandler : IRequestHandler<GetDriverPackageInsuranceStatusQuery, Result<DriverPackageInsuranceStatusDto>>
{
    private readonly IDriverWalletRepository _walletRepository;
    private readonly IDriverApplicationRepository _applicationRepository;
    private readonly IDriverPackageInsuranceFeeConfigRepository _configRepository;
    private readonly IMediator _mediator;

    public GetDriverPackageInsuranceStatusQueryHandler(
        IDriverWalletRepository walletRepository,
        IDriverApplicationRepository applicationRepository,
        IDriverPackageInsuranceFeeConfigRepository configRepository,
        IMediator mediator)
    {
        _walletRepository = walletRepository;
        _applicationRepository = applicationRepository;
        _configRepository = configRepository;
        _mediator = mediator;
    }

    public async Task<Result<DriverPackageInsuranceStatusDto>> Handle(GetDriverPackageInsuranceStatusQuery request, CancellationToken ct)
    {
        var vehicleType = await DriverVehicleTypeResolver.ResolveAsync(
            request.DriverId, _applicationRepository, _mediator, ct);

        var config = vehicleType is null ? null : await _configRepository.GetByVehicleTypeAsync(vehicleType, ct);
        var policy = await _walletRepository.GetInsurancePolicyByDriverIdAsync(request.DriverId, ct);

        return Result.Ok(new DriverPackageInsuranceStatusDto(
            request.DriverId,
            vehicleType,
            config?.Amount,
            policy?.PaidThroughYearNumber ?? 0,
            policy?.CoverageStartDate,
            policy?.CoverageEndDate,
            (policy?.Status ?? DriverPackageInsurancePolicyStatus.NotEnrolled).ToString()));
    }
}

/// <summary>
/// Issues the QR Ph code that pays a driver's next-due package-insurance premium straight into
/// the <b>platform</b> wallet — mirrors <c>CreateCashBondQrCommandHandler</c> exactly, except the
/// target year is server-computed as "the next unpaid one" rather than always the same fixed amount.
/// </summary>
public class CreateInsurancePremiumQrCommandHandler : IRequestHandler<CreateInsurancePremiumQrCommand, Result<PackageInsuranceQrDto>>
{
    /// <summary>
    /// Prefix marking a QR as paying a package-insurance premium. Byte-for-byte the same base62
    /// GUID-encoding algorithm <c>CreateCashBondQrCommandHandler</c> uses — see that class's
    /// remarks for why the id must be base62 and fit within 25 EMV characters. The policy year
    /// this transaction pays for is deliberately NOT encoded here (no room left in the budget);
    /// it lives on <see cref="WalletTransaction.PolicyYearNumber"/> instead.
    /// </summary>
    public const string ReferencePrefix = "PI";

    private const string Base62Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int EncodedIdLength = 22;

    public static string ReferenceFor(Guid transactionId)
    {
        var value = new System.Numerics.BigInteger(transactionId.ToByteArray(), isUnsigned: true, isBigEndian: true);

        var chars = new char[EncodedIdLength];
        for (var i = EncodedIdLength - 1; i >= 0; i--)
        {
            chars[i] = Base62Alphabet[(int)(value % 62)];
            value /= 62;
        }

        return ReferencePrefix + new string(chars);
    }

    /// <summary>Parses a reference label back to its transaction id, or null if it is not ours.</summary>
    public static Guid? TransactionIdFrom(string? referenceLabel)
    {
        if (referenceLabel is null
            || referenceLabel.Length != ReferencePrefix.Length + EncodedIdLength
            || !referenceLabel.StartsWith(ReferencePrefix, StringComparison.Ordinal))
            return null;

        var value = System.Numerics.BigInteger.Zero;
        foreach (var c in referenceLabel.AsSpan(ReferencePrefix.Length))
        {
            var digit = Base62Alphabet.IndexOf(c);
            if (digit < 0) return null;
            value = value * 62 + digit;
        }

        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length > 16) return null;

        var guidBytes = new byte[16];
        bytes.CopyTo(guidBytes, 16 - bytes.Length);
        return new Guid(guidBytes);
    }

    private readonly IDriverWalletRepository _walletRepository;
    private readonly IDriverApplicationRepository _applicationRepository;
    private readonly IDriverPackageInsuranceFeeConfigRepository _configRepository;
    private readonly IMediator _mediator;
    private readonly IPayMongoAccountsClient? _payMongoAccounts;
    private readonly ILogger<CreateInsurancePremiumQrCommandHandler>? _logger;

    public CreateInsurancePremiumQrCommandHandler(
        IDriverWalletRepository walletRepository,
        IDriverApplicationRepository applicationRepository,
        IDriverPackageInsuranceFeeConfigRepository configRepository,
        IMediator mediator,
        IPayMongoAccountsClient? payMongoAccounts = null,
        ILogger<CreateInsurancePremiumQrCommandHandler>? logger = null)
    {
        _walletRepository = walletRepository;
        _applicationRepository = applicationRepository;
        _configRepository = configRepository;
        _mediator = mediator;
        _payMongoAccounts = payMongoAccounts;
        _logger = logger;
    }

    public async Task<Result<PackageInsuranceQrDto>> Handle(CreateInsurancePremiumQrCommand request, CancellationToken ct)
    {
        if (_payMongoAccounts is null)
            return Result.Fail<PackageInsuranceQrDto>("Package-insurance payment is not available right now.");

        var vehicleType = await DriverVehicleTypeResolver.ResolveAsync(
            request.DriverId, _applicationRepository, _mediator, ct);
        if (string.IsNullOrWhiteSpace(vehicleType))
            return Result.Fail<PackageInsuranceQrDto>("No vehicle type on file for this driver.");

        var config = await _configRepository.GetByVehicleTypeAsync(vehicleType, ct);
        if (config is null)
            return Result.Fail<PackageInsuranceQrDto>($"No package-insurance amount configured for vehicle type '{vehicleType}'.");

        // Created on demand, same as cashbond: a driver paying insurance may not have onboarded to
        // BeeWallet yet, so this is often the first thing that needs a wallet row to hang the
        // transaction off.
        var wallet = await _walletRepository.GetWalletByDriverIdAsync(request.DriverId, ct)
                     ?? await _walletRepository.CreateWalletAsync(new DriverWallet(request.DriverId), ct);

        var policy = await _walletRepository.GetInsurancePolicyByDriverIdAsync(request.DriverId, ct)
                     ?? await _walletRepository.CreateInsurancePolicyAsync(new DriverPackageInsurancePolicy(request.DriverId), ct);

        // Server-computed, never client-supplied: strictly sequential, one year at a time.
        var targetYear = policy.PaidThroughYearNumber + 1;

        // One QR per outstanding attempt for this year, same reasoning as cashbond's retry guard.
        var existing = await _walletRepository.GetPendingInsurancePremiumPaymentAsync(wallet.Id, targetYear, ct);
        if (existing is not null)
        {
            var reissued = await _payMongoAccounts.GenerateWalletQrAsync(new GenerateWalletQrRequest(
                OnBehalfOfAccountId: null,
                Mode: WalletQrMode.P2P,
                Type: WalletQrType.Dynamic,
                Amount: existing.Amount,
                ReferenceLabel: CreateInsurancePremiumQrCommandHandler.ReferenceFor(existing.Id)), ct);

            return Result.Ok(new PackageInsuranceQrDto(
                reissued.QrString, GetBeeWalletTopUpQrQueryHandler.RenderQrPng(reissued.QrString),
                existing.Amount, CreateInsurancePremiumQrCommandHandler.ReferenceFor(existing.Id), reissued.ExpiresAt, targetYear));
        }

        // Claimed before the QR exists, so the reference label can carry this row's id, and the
        // target year is stamped on the row at the same moment - the new (WalletId, Type,
        // PolicyYearNumber) unique index turns a concurrent second claim for this same year into
        // a database-level conflict rather than a race the application has to notice on its own.
        WalletTransaction transaction;
        try
        {
            transaction = new WalletTransaction(
                wallet.Id, WalletTransactionType.PackageInsurancePayment, WalletBucket.PackageInsurance, config.Amount,
                WalletTransactionStatus.Pending, "Driver package-insurance premium", policyYearNumber: targetYear);
            await _walletRepository.CreateTransactionAsync(transaction, ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            // Lost a race to claim this year - a concurrent request already inserted the Pending
            // row. Reuse it rather than failing the caller.
            var winner = await _walletRepository.GetPendingInsurancePremiumPaymentAsync(wallet.Id, targetYear, ct);
            if (winner is null)
                return Result.Fail<PackageInsuranceQrDto>("Could not start your package-insurance payment. Please try again.");

            var reissued = await _payMongoAccounts.GenerateWalletQrAsync(new GenerateWalletQrRequest(
                OnBehalfOfAccountId: null,
                Mode: WalletQrMode.P2P,
                Type: WalletQrType.Dynamic,
                Amount: winner.Amount,
                ReferenceLabel: CreateInsurancePremiumQrCommandHandler.ReferenceFor(winner.Id)), ct);

            return Result.Ok(new PackageInsuranceQrDto(
                reissued.QrString, GetBeeWalletTopUpQrQueryHandler.RenderQrPng(reissued.QrString),
                winner.Amount, CreateInsurancePremiumQrCommandHandler.ReferenceFor(winner.Id), reissued.ExpiresAt, targetYear));
        }

        var reference = CreateInsurancePremiumQrCommandHandler.ReferenceFor(transaction.Id);
        var qr = await _payMongoAccounts.GenerateWalletQrAsync(new GenerateWalletQrRequest(
            OnBehalfOfAccountId: null,
            Mode: WalletQrMode.P2P,
            Type: WalletQrType.Dynamic,
            Amount: config.Amount,
            ReferenceLabel: reference), ct);

        transaction.SetProviderPaymentId(qr.QrId);
        await _walletRepository.UpdateTransactionAsync(transaction, ct);

        _logger?.LogInformation(
            "[PAYMONGO] [PACKAGE-INSURANCE] Issued QR {QrId} for {Amount} (year {Year}) to driver {DriverId} ({Reference})",
            qr.QrId, config.Amount, targetYear, request.DriverId, reference);

        return Result.Ok(new PackageInsuranceQrDto(
            qr.QrString, GetBeeWalletTopUpQrQueryHandler.RenderQrPng(qr.QrString),
            config.Amount, reference, qr.ExpiresAt, targetYear));
    }
}

/// <summary>
/// Settles a package-insurance premium once its QR has been paid into the platform wallet.
/// Mirrors <c>SettleCashBondQrPaymentCommandHandler</c>: idempotent on the transaction's own
/// status, amount verified rather than trusted, and — new here — the settling year must be
/// exactly the policy's next unpaid one.
/// </summary>
public class SettleInsurancePremiumQrPaymentCommandHandler : IRequestHandler<SettleInsurancePremiumQrPaymentCommand, Result>
{
    private readonly IDriverWalletRepository _walletRepository;
    private readonly TimeProvider _clock;
    private readonly ILogger<SettleInsurancePremiumQrPaymentCommandHandler>? _logger;

    public SettleInsurancePremiumQrPaymentCommandHandler(
        IDriverWalletRepository walletRepository,
        TimeProvider clock,
        ILogger<SettleInsurancePremiumQrPaymentCommandHandler>? logger = null)
    {
        _walletRepository = walletRepository;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result> Handle(SettleInsurancePremiumQrPaymentCommand request, CancellationToken ct)
    {
        var transactionId = CreateInsurancePremiumQrCommandHandler.TransactionIdFrom(request.ReferenceLabel);
        if (transactionId is null)
            return Result.Fail($"'{request.ReferenceLabel}' is not a package-insurance reference.");

        var found = await _walletRepository.GetInsurancePremiumPaymentWithPolicyAsync(transactionId.Value, ct);
        if (found is null)
            return Result.Fail($"No package-insurance payment matches {request.ReferenceLabel}.");

        var (wallet, policy, transaction) = found.Value;

        // Redelivery. PayMongo retries until it gets a 2xx, so this is the normal case, not an error.
        if (transaction.Status == WalletTransactionStatus.Completed)
            return Result.Ok();

        if (request.Amount < transaction.Amount)
        {
            _logger?.LogError(
                "[PAYMONGO] [PACKAGE-INSURANCE] {Reference} was paid {Paid} against {Due}; leaving it unsettled",
                request.ReferenceLabel, request.Amount, transaction.Amount);
            return Result.Fail("Package-insurance payment was short of the amount due.");
        }

        if (transaction.PolicyYearNumber is null)
        {
            _logger?.LogError(
                "[PAYMONGO] [PACKAGE-INSURANCE] {Reference} has no policy year on its transaction row",
                request.ReferenceLabel);
            return Result.Fail("Malformed package-insurance payment record.");
        }

        // Guards a second QR paid against a year an earlier one already settled: the money
        // arrived, so it must not be silently dropped, but the domain refuses a second payment.
        if (policy.PaidThroughYearNumber >= transaction.PolicyYearNumber)
        {
            _logger?.LogError(
                "[PAYMONGO] [PACKAGE-INSURANCE] {Reference} paid year {Year} but policy for driver "
                + "{DriverId} is already paid through year {PaidThrough}; needs review",
                request.ReferenceLabel, transaction.PolicyYearNumber, wallet.DriverId, policy.PaidThroughYearNumber);
            return Result.Fail("This package-insurance year was already paid; this payment needs review.");
        }

        try
        {
            policy.MarkYearPaid(transaction.PolicyYearNumber.Value, _clock.GetUtcNow().UtcDateTime);
        }
        catch (InvalidOperationException ex)
        {
            // Should be unreachable given the strictly-sequential target-year rule at claim time,
            // but a payment arriving for a year other than PaidThroughYearNumber + 1 must not
            // silently corrupt the counter.
            _logger?.LogError(ex,
                "[PAYMONGO] [PACKAGE-INSURANCE] Out-of-order settlement for {Reference}", request.ReferenceLabel);
            return Result.Fail("This package-insurance payment is out of order and needs manual reconciliation.");
        }

        transaction.SetProviderPaymentId(request.IdempotencyKey);
        transaction.MarkAsCompleted();

        try
        {
            await _walletRepository.SaveClaimedInsurancePremiumAsync(policy, transaction, ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            // Lost the race to a concurrent settlement of the same policy year. Not retried
            // in-process - PayMongo's own redelivery-until-2xx behavior is the retry mechanism,
            // same as every other settlement handler in this codebase.
            _logger?.LogWarning(
                "[PAYMONGO] [PACKAGE-INSURANCE] Concurrent settlement conflict for {Reference}; will retry on redelivery",
                request.ReferenceLabel);
            return Result.Fail("A concurrent payment already updated this policy; it will settle on redelivery.");
        }

        _logger?.LogInformation(
            "[PAYMONGO] [PACKAGE-INSURANCE] Settled {Reference} for {Amount} (year {Year}) on wallet {WalletId}",
            request.ReferenceLabel, transaction.Amount, transaction.PolicyYearNumber, wallet.Id);

        return Result.Ok();
    }
}
