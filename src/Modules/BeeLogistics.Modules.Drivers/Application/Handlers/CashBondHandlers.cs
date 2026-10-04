using BeeLogistics.Modules.Drivers.Application.DTOs;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Drivers.Infrastructure;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Drivers.Application.Handlers;

// ── Admin config CRUD (mirrors VehiclePricing's per-vehicle-type + versioning shape) ──────

public record CreateOrUpdateDriverCashBondConfigCommand(
    string VehicleType, decimal Amount, Guid? UpdatedByUserId, string? UpdatedByUserName)
    : IRequest<Result<DriverCashBondConfigDto>>;

public record GetDriverCashBondConfigsQuery : IRequest<Result<IReadOnlyList<DriverCashBondConfigDto>>>;

public record GetDriverCashBondConfigVersionsQuery(Guid DriverCashBondConfigId)
    : IRequest<Result<IReadOnlyList<DriverCashBondConfigVersionDto>>>;

// ── Driver-facing status + payment, admin-facing refund ────────────────────────────────

public record GetDriverCashBondStatusQuery(Guid DriverId) : IRequest<Result<DriverCashBondStatusDto>>;

public record RefundCashBondCommand(Guid DriverId, string PerformedBy) : IRequest<Result<DriverCashBondStatusDto>>;

/// <summary>Issues (or re-issues) the QR that pays this driver's cashbond into the platform wallet.</summary>
public record CreateCashBondQrCommand(Guid DriverId) : IRequest<Result<CashBondQrDto>>;

/// <summary>
/// Settles a cashbond after its QR was paid, driven by the parent account's <c>qr.paid</c> webhook.
/// </summary>
public record SettleCashBondQrPaymentCommand(string ReferenceLabel, decimal Amount, string IdempotencyKey)
    : IRequest<Result>;

/// <summary>
/// The driver's vehicle type, read from whichever of the two places actually holds it.
/// </summary>
/// <remarks>
/// <c>DriverApplications.VehicleType</c> is written by the onboarding flow and is the more
/// specific record, so it wins when present. But most drivers have no application row at all —
/// on dev, 43 of 46 driver wallets — and for them the vehicle type lives only on their Identity
/// user. Reading the application alone made <see cref="GetDriverCashBondStatusQueryHandler"/>
/// return a null <c>AmountDue</c> for nearly every driver, which the app renders as no cashbond
/// card rather than as an error, so the failure was silent on both sides.
/// </remarks>
internal static class DriverVehicleTypeResolver
{
    public static async Task<string?> ResolveAsync(
        Guid driverId,
        IDriverApplicationRepository applications,
        IMediator mediator,
        CancellationToken ct)
    {
        var application = await applications.GetByUserIdAsync(driverId.ToString(), ct);
        if (!string.IsNullOrWhiteSpace(application?.VehicleType))
            return application.VehicleType;

        // A driver id is an Identity user id — the controller's EnsureDriverAccess compares it
        // against the caller's NameIdentifier claim, so the two are the same value by construction.
        var fromIdentity = await mediator.Send(new GetUserVehicleTypeQuery(driverId), ct);
        return fromIdentity is { IsSuccess: true, Value: { } vehicleType }
               && !string.IsNullOrWhiteSpace(vehicleType)
            ? vehicleType
            : null;
    }
}

internal static class DriverCashBondMapper
{
    public static DriverCashBondConfigDto ToDto(DriverCashBondConfig config) => new(
        config.Id, config.VehicleType, config.Amount, config.Version, config.CreatedAt, config.UpdatedAt);

    public static DriverCashBondConfigVersionDto ToDto(DriverCashBondConfigVersion version) => new(
        version.Id, version.Version, version.Amount, version.ChangedByUserId, version.ChangedByUserName, version.CreatedAt);
}

public class CreateOrUpdateDriverCashBondConfigCommandHandler
    : IRequestHandler<CreateOrUpdateDriverCashBondConfigCommand, Result<DriverCashBondConfigDto>>
{
    private readonly IDriverCashBondConfigRepository _repository;
    private readonly DriversDbContext _context;

    public CreateOrUpdateDriverCashBondConfigCommandHandler(IDriverCashBondConfigRepository repository, DriversDbContext context)
    {
        _repository = repository;
        _context = context;
    }

    public async Task<Result<DriverCashBondConfigDto>> Handle(CreateOrUpdateDriverCashBondConfigCommand request, CancellationToken ct)
    {
        if (request.Amount <= 0)
            return Result.Fail<DriverCashBondConfigDto>("Amount must be positive");

        var existing = await _repository.GetByVehicleTypeAsync(request.VehicleType, ct);

        if (existing is null)
        {
            var config = new DriverCashBondConfig(request.VehicleType, request.Amount, request.UpdatedByUserId, request.UpdatedByUserName);

            var initialVersion = new DriverCashBondConfigVersion(
                config.Id, config.Version, config.Amount, request.UpdatedByUserId, request.UpdatedByUserName);

            _context.Set<DriverCashBondConfigVersion>().Add(initialVersion);
            _repository.Add(config);
            await _repository.SaveChangesAsync(ct);

            return Result.Ok(DriverCashBondMapper.ToDto(config));
        }

        existing.UpdateAmount(request.Amount, request.UpdatedByUserId, request.UpdatedByUserName);

        var newVersion = new DriverCashBondConfigVersion(
            existing.Id, existing.Version, existing.Amount, request.UpdatedByUserId, request.UpdatedByUserName);

        _context.Set<DriverCashBondConfigVersion>().Add(newVersion);
        _repository.Update(existing);
        await _repository.SaveChangesAsync(ct);

        return Result.Ok(DriverCashBondMapper.ToDto(existing));
    }
}

public class GetDriverCashBondConfigsQueryHandler : IRequestHandler<GetDriverCashBondConfigsQuery, Result<IReadOnlyList<DriverCashBondConfigDto>>>
{
    private readonly IDriverCashBondConfigRepository _repository;

    public GetDriverCashBondConfigsQueryHandler(IDriverCashBondConfigRepository repository) => _repository = repository;

    public async Task<Result<IReadOnlyList<DriverCashBondConfigDto>>> Handle(GetDriverCashBondConfigsQuery request, CancellationToken ct)
    {
        var configs = await _repository.GetAllAsync(ct);
        return Result.Ok<IReadOnlyList<DriverCashBondConfigDto>>(
            configs.OrderBy(c => c.VehicleType).Select(DriverCashBondMapper.ToDto).ToList());
    }
}

public class GetDriverCashBondConfigVersionsQueryHandler
    : IRequestHandler<GetDriverCashBondConfigVersionsQuery, Result<IReadOnlyList<DriverCashBondConfigVersionDto>>>
{
    private readonly IDriverCashBondConfigRepository _repository;

    public GetDriverCashBondConfigVersionsQueryHandler(IDriverCashBondConfigRepository repository) => _repository = repository;

    public async Task<Result<IReadOnlyList<DriverCashBondConfigVersionDto>>> Handle(GetDriverCashBondConfigVersionsQuery request, CancellationToken ct)
    {
        var versions = await _repository.GetVersionsByConfigIdAsync(request.DriverCashBondConfigId, ct);
        return Result.Ok<IReadOnlyList<DriverCashBondConfigVersionDto>>(
            versions.Select(DriverCashBondMapper.ToDto).ToList());
    }
}

public class GetDriverCashBondStatusQueryHandler : IRequestHandler<GetDriverCashBondStatusQuery, Result<DriverCashBondStatusDto>>
{
    private readonly IDriverWalletRepository _walletRepository;
    private readonly IDriverApplicationRepository _applicationRepository;
    private readonly IDriverCashBondConfigRepository _configRepository;
    private readonly IMediator _mediator;

    public GetDriverCashBondStatusQueryHandler(
        IDriverWalletRepository walletRepository,
        IDriverApplicationRepository applicationRepository,
        IDriverCashBondConfigRepository configRepository,
        IMediator mediator)
    {
        _walletRepository = walletRepository;
        _applicationRepository = applicationRepository;
        _configRepository = configRepository;
        _mediator = mediator;
    }

    public async Task<Result<DriverCashBondStatusDto>> Handle(GetDriverCashBondStatusQuery request, CancellationToken ct)
    {
        var wallet = await _walletRepository.GetWalletByDriverIdAsync(request.DriverId, ct);
        var vehicleType = await DriverVehicleTypeResolver.ResolveAsync(
            request.DriverId, _applicationRepository, _mediator, ct);

        var config = vehicleType is null ? null : await _configRepository.GetByVehicleTypeAsync(vehicleType, ct);

        var balance = wallet?.CashBondBalance ?? 0m;
        return Result.Ok(new DriverCashBondStatusDto(request.DriverId, vehicleType, config?.Amount, balance, balance > 0));
    }
}

/// <summary>
/// Admin-triggered release of a driver's cashbond back to their own child wallet on offboarding —
/// the reverse of <see cref="CreateCashBondQrCommandHandler"/>.
/// </summary>
public class RefundCashBondCommandHandler : IRequestHandler<RefundCashBondCommand, Result<DriverCashBondStatusDto>>
{
    private readonly IDriverWalletRepository _walletRepository;
    private readonly IPayMongoAccountsClient? _payMongoAccounts;
    private readonly ILogger<RefundCashBondCommandHandler>? _logger;

    public RefundCashBondCommandHandler(
        IDriverWalletRepository walletRepository,
        IPayMongoAccountsClient? payMongoAccounts = null,
        ILogger<RefundCashBondCommandHandler>? logger = null)
    {
        _walletRepository = walletRepository;
        _payMongoAccounts = payMongoAccounts;
        _logger = logger;
    }

    public async Task<Result<DriverCashBondStatusDto>> Handle(RefundCashBondCommand request, CancellationToken ct)
    {
        var wallet = await _walletRepository.GetWalletByDriverIdAsync(request.DriverId, ct);
        if (wallet is null)
            return Result.Fail<DriverCashBondStatusDto>("Wallet not found.");

        if (!wallet.HasPaidCashBond)
            return Result.Fail<DriverCashBondStatusDto>("There is no cashbond to refund.");

        if (!wallet.UsesPayMongoWallet || _payMongoAccounts is null)
            return Result.Fail<DriverCashBondStatusDto>("Driver's PayMongo wallet is not active; refund cannot be routed.");

        var amount = wallet.CashBondBalance;

        var transaction = new WalletTransaction(
            wallet.Id, WalletTransactionType.CashBondRefund, WalletBucket.CashBond, amount,
            WalletTransactionStatus.Pending, $"Cashbond refund (by {request.PerformedBy})");
        await _walletRepository.CreateTransactionAsync(transaction, ct);

        var reference = $"cashbond-refund-{transaction.Id:N}";
        var transfer = await _payMongoAccounts.TransferToChildAsync(
            wallet.PayMongoAccountId!, wallet.PayMongoAccountNumber!,
            wallet.AccountHolderName ?? "Driver", amount, reference, "Driver cashbond refund", ct);

        transaction.SetProviderPaymentId(transfer.TransferId);

        if (transfer.Failed)
        {
            transaction.MarkAsFailed();
            await _walletRepository.UpdateTransactionAsync(transaction, ct);

            _logger?.LogError(
                "[PAYMONGO] [CASHBOND] Refund of {Amount} failed for driver {DriverId}: {Reason}",
                amount, request.DriverId, transfer.ProviderErrorMessage ?? "no reason given");

            return Result.Fail<DriverCashBondStatusDto>("Could not refund the cashbond. Please try again.");
        }

        if (!transfer.Succeeded)
        {
            _logger?.LogWarning(
                "[PAYMONGO] [CASHBOND] Refund of {Amount} for driver {DriverId} returned status "
                + "'{Status}' ({TransferId}); leaving the transaction pending.",
                amount, request.DriverId, transfer.Status, transfer.TransferId);

            await _walletRepository.UpdateTransactionAsync(transaction, ct);

            return Result.Fail<DriverCashBondStatusDto>("The refund is still processing.");
        }

        wallet.ClearCashBondOnRefund();
        transaction.MarkAsCompleted();
        await _walletRepository.SaveClaimedTransactionAsync(wallet, transaction, ct);

        _logger?.LogInformation(
            "[PAYMONGO] [CASHBOND] Refunded {Amount} to driver {DriverId} ({TransferId})",
            amount, request.DriverId, transfer.TransferId);

        return Result.Ok(new DriverCashBondStatusDto(request.DriverId, null, null, 0m, false));
    }
}

/// <summary>
/// Issues the QR Ph code that pays a driver's cashbond straight into the <b>platform</b> wallet.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the original sweep from the driver's own child wallet, which had the dependency
/// backwards. The cashbond is due before a driver can be offered bookings — which is before
/// BeeWallet onboarding — so at that point there is no child wallet to sweep from, and the old
/// handler refused with "Your PayMongo wallet must be activated". The correct order is: cashbond,
/// then BeeWallet onboarding (child account plus its account-opening fee), then bookings.
/// </para>
/// <para>
/// Paying the platform directly also fixes how the payment settles. A child account's transaction
/// webhooks never reach us, which is why the sweep had to be polled and why three pending sweeps
/// could stack up unresolved; the platform account's webhooks do arrive, so <c>qr.paid</c> settles
/// this without a poller.
/// </para>
/// </remarks>
public class CreateCashBondQrCommandHandler : IRequestHandler<CreateCashBondQrCommand, Result<CashBondQrDto>>
{
    /// <summary>
    /// Prefix marking a QR as paying a cashbond. The reference label is the only routing key a
    /// platform QR has, so it also carries the transaction id.
    /// </summary>
    /// <remarks>
    /// <b>The label must fit in 25 characters.</b> EMV MPM caps field 62/05 (Reference Label) at
    /// that, and PayMongo enforces it as a 500 from QR generation, not a validation error:
    /// <c>EMV MPM QR validation fail: 62/05 (Reference Label) length exceeds maximum of 25
    /// characters: 41</c>. The obvious <c>cashbond-{id:N}</c> is 41 and never worked.
    /// <para>
    /// So the id is base62 rather than hex: 22 characters for all 128 bits, plus a 2-character
    /// prefix, is 24. Base62 over base64url because the alphabet is purely alphanumeric — EMV
    /// fields are not a place to find out whether <c>-</c> and <c>_</c> survive a payment rail.
    /// Truncating the id instead was the alternative, and it trades a guaranteed fit for a
    /// collision that would settle one driver's cashbond against another's payment.
    /// </para>
    /// </remarks>
    public const string ReferencePrefix = "CB";

    /// <summary>Maximum length EMV MPM allows for a reference label.</summary>
    public const int MaxReferenceLabelLength = 25;

    private const string Base62Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int EncodedIdLength = 22;

    public static string ReferenceFor(Guid transactionId)
    {
        var value = new System.Numerics.BigInteger(transactionId.ToByteArray(), isUnsigned: true, isBigEndian: true);

        // Fixed width, left-padded: a small id would otherwise encode short and decode to a
        // different value once the padding was stripped.
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

        // Left-pad back to 16: ToByteArray drops leading zero bytes.
        var guidBytes = new byte[16];
        bytes.CopyTo(guidBytes, 16 - bytes.Length);
        return new Guid(guidBytes);
    }

    private readonly IDriverWalletRepository _walletRepository;
    private readonly IDriverApplicationRepository _applicationRepository;
    private readonly IDriverCashBondConfigRepository _configRepository;
    private readonly IMediator _mediator;
    private readonly IPayMongoAccountsClient? _payMongoAccounts;
    private readonly ILogger<CreateCashBondQrCommandHandler>? _logger;

    public CreateCashBondQrCommandHandler(
        IDriverWalletRepository walletRepository,
        IDriverApplicationRepository applicationRepository,
        IDriverCashBondConfigRepository configRepository,
        IMediator mediator,
        IPayMongoAccountsClient? payMongoAccounts = null,
        ILogger<CreateCashBondQrCommandHandler>? logger = null)
    {
        _walletRepository = walletRepository;
        _applicationRepository = applicationRepository;
        _configRepository = configRepository;
        _mediator = mediator;
        _payMongoAccounts = payMongoAccounts;
        _logger = logger;
    }

    public async Task<Result<CashBondQrDto>> Handle(CreateCashBondQrCommand request, CancellationToken ct)
    {
        if (_payMongoAccounts is null)
            return Result.Fail<CashBondQrDto>("Cashbond payment is not available right now.");

        var vehicleType = await DriverVehicleTypeResolver.ResolveAsync(
            request.DriverId, _applicationRepository, _mediator, ct);
        if (string.IsNullOrWhiteSpace(vehicleType))
            return Result.Fail<CashBondQrDto>("No vehicle type on file for this driver.");

        var config = await _configRepository.GetByVehicleTypeAsync(vehicleType, ct);
        if (config is null)
            return Result.Fail<CashBondQrDto>($"No cashbond amount configured for vehicle type '{vehicleType}'.");

        // Created on demand: a driver paying their cashbond has not onboarded to BeeWallet yet, so
        // this is often the first thing that needs a wallet row to hang the transaction off.
        var wallet = await _walletRepository.GetWalletByDriverIdAsync(request.DriverId, ct)
                     ?? await _walletRepository.CreateWalletAsync(new DriverWallet(request.DriverId), ct);

        if (wallet.HasPaidCashBond)
            return Result.Fail<CashBondQrDto>("Your cashbond is already paid.");

        // One QR per outstanding attempt. Issuing a fresh one each time would leave several live
        // codes for the same debt, and a driver who scanned an older one would pay against a
        // transaction we had stopped watching.
        var existing = await _walletRepository.GetPendingCashBondPaymentAsync(wallet.Id, ct);
        if (existing is not null)
        {
            var reissued = await _payMongoAccounts.GenerateWalletQrAsync(new GenerateWalletQrRequest(
                OnBehalfOfAccountId: null,          // null = the platform's own wallet
                Mode: WalletQrMode.P2P,
                Type: WalletQrType.Dynamic,
                Amount: existing.Amount,
                ReferenceLabel: ReferenceFor(existing.Id)), ct);

            return Result.Ok(new CashBondQrDto(
                reissued.QrString, GetBeeWalletTopUpQrQueryHandler.RenderQrPng(reissued.QrString),
                existing.Amount, ReferenceFor(existing.Id), reissued.ExpiresAt));
        }

        // Claimed before the QR exists, so the reference label can carry this row's id — that label
        // is what the webhook routes on.
        var transaction = new WalletTransaction(
            wallet.Id, WalletTransactionType.CashBondPayment, WalletBucket.CashBond, config.Amount,
            WalletTransactionStatus.Pending, "Driver cashbond payment");
        await _walletRepository.CreateTransactionAsync(transaction, ct);

        var reference = ReferenceFor(transaction.Id);
        var qr = await _payMongoAccounts.GenerateWalletQrAsync(new GenerateWalletQrRequest(
            OnBehalfOfAccountId: null,
            Mode: WalletQrMode.P2P,
            Type: WalletQrType.Dynamic,
            Amount: config.Amount,
            ReferenceLabel: reference), ct);

        transaction.SetProviderPaymentId(qr.QrId);
        await _walletRepository.UpdateTransactionAsync(transaction, ct);

        _logger?.LogInformation(
            "[PAYMONGO] [CASHBOND] Issued QR {QrId} for {Amount} to driver {DriverId} ({Reference})",
            qr.QrId, config.Amount, request.DriverId, reference);

        return Result.Ok(new CashBondQrDto(
            qr.QrString, GetBeeWalletTopUpQrQueryHandler.RenderQrPng(qr.QrString),
            config.Amount, reference, qr.ExpiresAt));
    }
}

/// <summary>
/// Settles a cashbond once its QR has been paid into the platform wallet.
/// </summary>
/// <remarks>
/// Idempotent on the transaction's own status rather than on a provider key: a redelivered webhook
/// finds the row already Completed and does nothing. The amount is checked rather than trusted —
/// the QR is dynamic so the payer cannot change it, but a short payment must not mark a cashbond
/// paid.
/// </remarks>
public class SettleCashBondQrPaymentCommandHandler : IRequestHandler<SettleCashBondQrPaymentCommand, Result>
{
    private readonly IDriverWalletRepository _walletRepository;
    private readonly ILogger<SettleCashBondQrPaymentCommandHandler>? _logger;

    public SettleCashBondQrPaymentCommandHandler(
        IDriverWalletRepository walletRepository,
        ILogger<SettleCashBondQrPaymentCommandHandler>? logger = null)
    {
        _walletRepository = walletRepository;
        _logger = logger;
    }

    public async Task<Result> Handle(SettleCashBondQrPaymentCommand request, CancellationToken ct)
    {
        var transactionId = CreateCashBondQrCommandHandler.TransactionIdFrom(request.ReferenceLabel);
        if (transactionId is null)
            return Result.Fail($"'{request.ReferenceLabel}' is not a cashbond reference.");

        var found = await _walletRepository.GetCashBondPaymentWithWalletAsync(transactionId.Value, ct);
        if (found is null)
            return Result.Fail($"No cashbond payment matches {request.ReferenceLabel}.");

        var (wallet, transaction) = found.Value;

        // Redelivery. PayMongo retries until it gets a 2xx, so this is the normal case, not an error.
        if (transaction.Status == WalletTransactionStatus.Completed)
            return Result.Ok();

        if (request.Amount < transaction.Amount)
        {
            _logger?.LogError(
                "[PAYMONGO] [CASHBOND] {Reference} was paid {Paid} against {Due}; leaving it unsettled",
                request.ReferenceLabel, request.Amount, transaction.Amount);
            return Result.Fail("Cashbond payment was short of the amount due.");
        }

        // Guards a second QR paid against a cashbond an earlier one already settled: the money
        // arrived, so it must not be silently dropped, but the domain refuses a second payment.
        if (wallet.HasPaidCashBond)
        {
            _logger?.LogError(
                "[PAYMONGO] [CASHBOND] {Reference} paid {Amount} but wallet {WalletId} is already "
                + "paid; needs a refund",
                request.ReferenceLabel, request.Amount, wallet.Id);
            return Result.Fail("Cashbond was already paid; this payment needs refunding.");
        }

        wallet.MarkCashBondPaid(transaction.Amount);
        transaction.SetProviderPaymentId(request.IdempotencyKey);
        transaction.MarkAsCompleted();
        await _walletRepository.SaveClaimedTransactionAsync(wallet, transaction, ct);

        _logger?.LogInformation(
            "[PAYMONGO] [CASHBOND] Settled {Reference} for {Amount} on wallet {WalletId}",
            request.ReferenceLabel, transaction.Amount, wallet.Id);

        return Result.Ok();
    }
}
