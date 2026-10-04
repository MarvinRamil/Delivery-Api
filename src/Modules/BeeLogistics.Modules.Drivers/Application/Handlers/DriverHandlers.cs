using BeeLogistics.Modules.Drivers.Application.DTOs;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Payment.Application.Banks;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Revenue.Application.Interfaces;
using BeeLogistics.Modules.Revenue.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Infrastructure;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Modules.Identity.Domain;
using MassTransit;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Drivers.Application.Handlers;

// Queries
public record GetDriverEarningsQuery(Guid DriverId, DateTime? StartDate = null, DateTime? EndDate = null) 
    : IRequest<Result<DriverEarningsDto>>;

public record GetDriverEarningsHistoryQuery(Guid DriverId, DateTime? StartDate = null, DateTime? EndDate = null, int? Limit = null)
    : IRequest<Result<DriverEarningsHistoryDto>>;

public record GetDriverWalletQuery(Guid DriverId) 
    : IRequest<Result<DriverWalletDto>>;

public record GetWalletTransactionsQuery(
    Guid DriverId, 
    DateTime? StartDate = null, 
    DateTime? EndDate = null, 
    WalletTransactionType? Type = null) 
    : IRequest<Result<IReadOnlyList<WalletTransactionDto>>>;

public record GetDriverWithdrawalRequestsQuery(Guid DriverId, WithdrawalStatus? Status = null)
    : IRequest<Result<IReadOnlyList<WithdrawalRequestDto>>>;

public record GetDriverMissionsQuery(Guid DriverId, MissionStatus? Status = null) 
    : IRequest<Result<IReadOnlyList<DriverMissionDto>>>;

// Commands
public record RequestWithdrawalCommand(
    Guid DriverId, 
    decimal Amount, 
    Guid? SavedWithdrawalMethodId = null, // Optional: use saved withdrawal method
    string? BankAccountNumber = null, // Required if SavedWithdrawalMethodId is not provided
    string? BankName = null, // Required if SavedWithdrawalMethodId is not provided
    string? AccountHolderName = null, // Required if SavedWithdrawalMethodId is not provided
    string? BankCode = null, // Catalog code from GET /api/payments/banks; falls back to BankName for older app builds
    string? IdempotencyKey = null, // Optional: duplicate requests with same key return existing withdrawal
    WithdrawalDestinationType DestinationType = WithdrawalDestinationType.BankAccount,
    string? QrString = null) // Required when DestinationType is QrPh; replaces the bank fields
    : IRequest<Result<WithdrawalRequestDto>>;

public record ClaimMissionRewardCommand(Guid DriverId, Guid MissionId) 
    : IRequest<Result<DriverMissionDto>>;

public record CreateDriverTopUpCommand(Guid DriverId, decimal Amount, string? PayerEmail = null, string? Description = null, string? IdempotencyKey = null)
    : IRequest<Result<DriverTopUpDto>>;

public record GetDriverTopUpQuery(Guid DriverId, Guid TopUpId)
    : IRequest<Result<DriverTopUpDto>>;

public record GetDriverTopUpHistoryQuery(Guid DriverId)
    : IRequest<Result<IReadOnlyList<DriverTopUpDto>>>;

public record TransferWalletBalanceCommand(Guid DriverId, WalletBucket From, WalletBucket To, decimal Amount)
    : IRequest<Result<DriverWalletDto>>;

public record GetCashJobEligibilityQuery(Guid DriverId)
    : IRequest<Result<CashJobEligibilityDto>>;

/// <param name="Currency">
/// ISO code the provider charged in, when it reports one. Null means unknown, which is not the
/// same as "matched" — see the guard in the handler.
/// </param>
public record ProcessDriverTopUpWebhookCommand(string Provider, string ProviderPaymentId, string Status, DateTime? PaidAt, decimal? PaidAmount, string? ExternalId = null, string? Currency = null)
    : IRequest<Result>;

/// <summary>
/// The driver attempted a top-up payment and the provider declined it.
/// </summary>
/// <param name="ProviderPaymentId">The provider's id for the failed payment attempt, not the checkout.</param>
/// <param name="ExternalId">Our reference number, which is how the attempt is joined back to the top-up.</param>
public record RecordDriverTopUpPaymentFailureCommand(
    string Provider,
    string ProviderPaymentId,
    string? ExternalId,
    string? Reason,
    DateTime? FailedAt)
    : IRequest<Result>;

/// <summary>
/// Operator recovery: credit a top-up the driver paid for that the automated paths could not
/// settle (GitLab #64). Until this existed, the only fix was hand-written SQL.
/// </summary>
/// <param name="Amount">
/// Null credits the amount the top-up was created for. Supplied explicitly to resolve an
/// amount mismatch, where what the provider actually took differs from what we asked for.
/// </param>
public record AdminCreditDriverTopUpCommand(Guid TopUpId, decimal? Amount, string Reason, string PerformedBy)
    : IRequest<Result<DriverTopUpDto>>;

public record ApplyCashSettlementDebitCommand(Guid DriverId, Guid BookingId, decimal Amount)
    : IRequest<Result>;

// GetDriversEligibleForCashJobQuery is declared in Shared.Contracts, not here: dispatch lives in
// Bookings, which cannot reference this module (Drivers already references Bookings). Its handler
// is below.

public record GetDriverWalletRiskQuery()
    : IRequest<Result<IReadOnlyList<DriverWalletDto>>>;

public record GetAllDriverTopUpsQuery(DateTime? From = null, DateTime? To = null, DriverTopUpStatus? Status = null)
    : IRequest<Result<IReadOnlyList<DriverTopUpDto>>>;

public record CancelDriverTopUpCommand(Guid DriverId, Guid TopUpId, string? Reason = null)
    : IRequest<Result<DriverTopUpDto>>;

/// <summary>
/// Processes Xendit disbursement/payout webhook (payout.succeeded, payout.failed, etc.).
/// Updates withdrawal status, wallet PendingPayout, and wallet transaction; publishes accounting events.
/// Idempotent: if withdrawal is already Completed/Failed, no-op.
/// </summary>
public record ProcessDisbursementWebhookCommand(string Provider, string DisbursementId, string Event, string Status, string? FailureReason = null)
    : IRequest<Result>;

/// <summary>
/// Releases a reservation the provider has no record of: returns the held funds to the
/// driver's balance and marks the withdrawal Failed. Only ever issued by reconciliation
/// after a positive "no such transfer" answer — see issue #90.
/// </summary>
public record ReleaseWithdrawalReservationCommand(Guid WithdrawalId, string Reason) : IRequest<Result>;

/// <summary>
/// Attaches a provider disbursement id to a reservation that turned out to have reached the
/// provider after all (the create response was lost, not the transfer), so the normal
/// status-tracking path can take over.
/// </summary>
public record AdoptWithdrawalDisbursementCommand(
    Guid WithdrawalId, string Provider, string ProviderDisbursementId, decimal Amount, string RawStatus, string? FailureCode = null)
    : IRequest<Result>;

// Handlers

/// <summary>
/// Gets driver earnings from completed bookings (independent-driver model).
/// In the independent-driver model, drivers earn from completed bookings where they are the SelectedDriverId.
/// Earnings = 80% of FinalFare + 100% of Tips
/// </summary>
public class GetDriverEarningsQueryHandler : IRequestHandler<GetDriverEarningsQuery, Result<DriverEarningsDto>>
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IPaymentRepository _paymentRepository;
    private readonly DriverWalletOptions _options;

    public GetDriverEarningsQueryHandler(
        IBookingRepository bookingRepository,
        IPaymentRepository paymentRepository,
        IOptions<DriverWalletOptions> options)
    {
        _bookingRepository = bookingRepository;
        _paymentRepository = paymentRepository;
        _options = options.Value;
    }

    public async Task<Result<DriverEarningsDto>> Handle(GetDriverEarningsQuery request, CancellationToken ct)
    {
        var filteredBookings = await _bookingRepository.GetCompletedByDriverIdAsync(
            request.DriverId, request.StartDate, request.EndDate, ct);
        var bookingIds = filteredBookings.Select(b => b.Id).ToList();
        if (bookingIds.Count == 0)
        {
            var emptyDto = new DriverEarningsDto(0, 0, 0, 0, new List<EarningsBreakdownDto>());
            return Result.Ok(emptyDto);
        }

        var paymentsForBookings = await _paymentRepository.GetByBookingIdsAsync(bookingIds, ct);
        var completedPayments = paymentsForBookings.Where(p => p.Status == PaymentStatus.Paid).ToList();

        var today = DateTime.UtcNow.Date;
        var weekStart = today.AddDays(-(int)today.DayOfWeek);
        var monthStart = new DateTime(today.Year, today.Month, 1);

        // Per-booking split via EarningsSplit, then summed - NOT sum(amount) * rate. The wallet is
        // credited one booking at a time with the commission rounded per booking, so applying the
        // rate to a total (or leaving it unrounded, as this did) shows the driver a figure that
        // disagrees with their actual balance by a centavo or more.
        decimal DriverShare(BeeLogistics.Modules.Payment.Domain.Payment payment) =>
            EarningsSplit.For(payment.Amount, _options.PlatformCommissionRate).DriverAmount;

        var todayEarnings = completedPayments
            .Where(p => p.PaidAt.HasValue && p.PaidAt.Value.Date == today)
            .Sum(DriverShare);

        var weekEarnings = completedPayments
            .Where(p => p.PaidAt.HasValue && p.PaidAt.Value.Date >= weekStart)
            .Sum(DriverShare);

        var monthEarnings = completedPayments
            .Where(p => p.PaidAt.HasValue && p.PaidAt.Value.Year == today.Year && p.PaidAt.Value.Month == today.Month)
            .Sum(DriverShare);

        var totalEarnings = completedPayments.Sum(DriverShare);

        // Build breakdown by date
        var breakdown = completedPayments
            .Where(p => p.PaidAt.HasValue)
            .GroupBy(p => p.PaidAt!.Value.Date)
            .Select(g => new EarningsBreakdownDto(
                g.Key,
                g.Sum(DriverShare),
                g.Count()
            ))
            .OrderByDescending(b => b.Date)
            .ToList();

        var dto = new DriverEarningsDto(
            todayEarnings,
            weekEarnings,
            monthEarnings,
            totalEarnings,
            breakdown
        );

        return Result.Ok(dto);
    }
}

/// <summary>
/// Gets driver earnings history with detailed 5% platform fee breakdown for verification.
/// </summary>
public class GetDriverEarningsHistoryQueryHandler : IRequestHandler<GetDriverEarningsHistoryQuery, Result<DriverEarningsHistoryDto>>
{
    private readonly IPlatformCommissionRepository _commissionRepository;

    public GetDriverEarningsHistoryQueryHandler(IPlatformCommissionRepository commissionRepository)
    {
        _commissionRepository = commissionRepository;
    }

    public async Task<Result<DriverEarningsHistoryDto>> Handle(GetDriverEarningsHistoryQuery request, CancellationToken ct)
    {
        var commissions = await _commissionRepository.GetByDriverIdAsync(
            request.DriverId, request.StartDate, request.EndDate, ct);

        // A refunded booking's commission is flipped to Reversed (PaymentRefundedRevenueConsumer)
        // but the row itself is kept for audit rather than deleted. Excluding it here is what
        // stops this verification screen from still showing the gross/commission/net of a booking
        // whose payment no longer stands.
        var list = commissions
            .Where(c => c.Status != PlatformCommissionStatus.Reversed)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new EarningsHistoryItemDto(
                c.BookingId,
                c.CreatedAt,
                c.PaymentMethod,
                c.GrossAmount,
                c.CommissionRate * 100,
                c.CommissionAmount,
                c.DriverAmount))
            .ToList();

        if (request.Limit.HasValue && request.Limit.Value > 0 && list.Count > request.Limit.Value)
            list = list.Take(request.Limit.Value).ToList();

        var totalGross = list.Sum(x => x.GrossAmount);
        var totalPlatformFee = list.Sum(x => x.PlatformFeeAmount);
        var totalNet = list.Sum(x => x.NetAmount);

        var dto = new DriverEarningsHistoryDto(list, totalGross, totalPlatformFee, totalNet);
        return Result.Ok(dto);
    }
}

public class GetDriverWalletQueryHandler : IRequestHandler<GetDriverWalletQuery, Result<DriverWalletDto>>
{
    /// <summary>
    /// How long the balance read is given before the stored figure is served instead.
    /// </summary>
    /// <remarks>
    /// This is the app's most-hit endpoint and it is on the path of every wallet screen, so a slow
    /// provider must cost the driver a slightly stale number, not a spinner. Two seconds is well
    /// clear of a normal PayMongo response and still under the point where the screen feels stuck.
    /// </remarks>
    private static readonly TimeSpan BalanceReadTimeout = TimeSpan.FromSeconds(2);

    private readonly IDriverWalletRepository _repository;
    private readonly DriverWalletOptions _options;
    private readonly ILogger<GetDriverWalletQueryHandler>? _logger;

    /// <summary>
    /// Reads the live balance for a migrated driver. Optional for the same reason
    /// <see cref="RequestWithdrawalCommandHandler"/>'s PayMongo service is: where it is not
    /// registered, this falls back to the stored balance rather than failing to construct.
    /// </summary>
    private readonly IPayMongoAccountsClient? _accounts;

    public GetDriverWalletQueryHandler(
        IDriverWalletRepository repository,
        IOptions<DriverWalletOptions> options,
        ILogger<GetDriverWalletQueryHandler>? logger = null,
        IPayMongoAccountsClient? accounts = null)
    {
        _repository = repository;
        _options = options.Value;
        _logger = logger;
        _accounts = accounts;
    }

    // Deliberately uncached. This DTO was previously held for 5 minutes, and only the top-up
    // webhook ever invalidated it - so a transfer, a withdrawal, an earning credit or a cash
    // settlement all left the driver staring at a balance that no longer existed. Worse, the
    // cash-job eligibility endpoint reads the wallet directly, so the same screen showed a stale
    // balance next to a fresh top-up figure. Invalidation is a rule every future writer has to
    // remember, and four of them already didn't; a single indexed lookup is the cheaper mistake.
    public async Task<Result<DriverWalletDto>> Handle(GetDriverWalletQuery request, CancellationToken ct)
    {
        var wallet = await _repository.GetWalletByDriverIdAsync(request.DriverId, ct);
        
        if (wallet == null)
        {
            // Create wallet if it doesn't exist
            var newWallet = new DriverWallet(request.DriverId);
            wallet = await _repository.CreateWalletAsync(newWallet, ct);
        }

        var dto = new DriverWalletDto(
            wallet.Id,
            wallet.DriverId,
            await ReadPersonalBalanceAsync(wallet, ct),
            wallet.TopUpBalance,
            wallet.PendingPayout,
            wallet.IsEligibleForCashJobs(await _repository.HasUnpaidCommissionAsync(wallet.Id, ct)),
            wallet.BankAccountNumber,
            wallet.BankName,
            wallet.AccountHolderName,
            wallet.LastUpdatedAt
        );

        return Result.Ok(dto);
    }

    /// <summary>
    /// The driver's personal balance, read from PayMongo for a migrated wallet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a migrated driver the money is at PayMongo, not here: once earnings are pushed into the
    /// child wallet it holds both their earnings and their QR top-ups, so its available balance
    /// <i>is</i> the personal balance and our column is a ledger of how it got there. The column
    /// lags because a <c>qr.paid</c> is emitted on the child account, where the parent's webhook
    /// never sees it — so a top-up could sit at PayMongo indefinitely while the app showed the old
    /// figure and the driver, reasonably, paid again (issue #101).
    /// </para>
    /// <para>
    /// Only the personal balance comes from PayMongo. The Cash Wallet float is funded by checkout
    /// and never leaves us, and cash-job eligibility is decided on that float, so neither changes.
    /// </para>
    /// <para>
    /// Every failure keeps the stored value. A stale balance is what the driver sees today; an
    /// error screen because a provider was slow would be worse than the bug being fixed.
    /// </para>
    /// </remarks>
    private async Task<decimal> ReadPersonalBalanceAsync(DriverWallet wallet, CancellationToken ct)
    {
        if (!wallet.UsesPayMongoWallet || _accounts is null || wallet.PayMongoAccountId is null)
            return wallet.Balance;

        try
        {
            // Linked, not replaced: a cancelled request should still abandon the call rather than
            // waiting out the timeout.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(BalanceReadTimeout);

            var remote = await _accounts.GetWalletAsync(
                wallet.PayMongoAccountId, includeBalance: true, ct: timeout.Token);

            if (remote?.AvailableBalance is { } available)
                return available;

            _logger?.LogWarning(
                "[PAYMONGO] [WALLET] No balance returned for driver {DriverId}; serving the stored figure",
                wallet.DriverId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Includes the timeout above, which surfaces as OperationCanceledException on a token
            // the caller did not cancel. A caller-cancelled request is left to propagate: nobody is
            // waiting for the answer.
            _logger?.LogWarning(ex,
                "[PAYMONGO] [WALLET] Could not read the live balance for driver {DriverId}; serving the stored figure",
                wallet.DriverId);
        }

        return wallet.Balance;
    }
}

public class GetWalletTransactionsQueryHandler : IRequestHandler<GetWalletTransactionsQuery, Result<IReadOnlyList<WalletTransactionDto>>>
{
    private readonly IDriverWalletRepository _repository;

    public GetWalletTransactionsQueryHandler(IDriverWalletRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<WalletTransactionDto>>> Handle(GetWalletTransactionsQuery request, CancellationToken ct)
    {
        var wallet = await _repository.GetWalletByDriverIdAsync(request.DriverId, ct);
        if (wallet == null)
        {
            return Result.Ok<IReadOnlyList<WalletTransactionDto>>(new List<WalletTransactionDto>());
        }

        var transactions = await _repository.GetTransactionsByWalletIdAsync(
            wallet.Id,
            request.StartDate,
            request.EndDate,
            request.Type,
            ct
        );

        var dtos = transactions.Select(t => new WalletTransactionDto(
            t.Id,
            t.WalletId,
            t.Type,
            t.Bucket,
            t.Amount,
            t.Status,
            t.Description,
            t.RelatedBookingId,
            t.RelatedWithdrawalRequestId,
            t.TransactionDate
        )).ToList();

        return Result.Ok<IReadOnlyList<WalletTransactionDto>>(dtos);
    }
}

public class GetDriverWithdrawalRequestsQueryHandler : IRequestHandler<GetDriverWithdrawalRequestsQuery, Result<IReadOnlyList<WithdrawalRequestDto>>>
{
    private readonly IDriverWalletRepository _repository;

    public GetDriverWithdrawalRequestsQueryHandler(IDriverWalletRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<WithdrawalRequestDto>>> Handle(GetDriverWithdrawalRequestsQuery request, CancellationToken ct)
    {
        var list = await _repository.GetWithdrawalRequestsByDriverIdAsync(request.DriverId, request.Status, ct);
        var dtos = list.Select(wr => new WithdrawalRequestDto(
            wr.Id,
            wr.DriverId,
            wr.WalletId,
            wr.Amount,
            wr.Status,
            MaskAccountNumber(wr.BankAccountNumber),
            wr.BankName,
            wr.AccountHolderName,
            wr.RejectionReason,
            wr.RequestedAt,
            wr.ProcessedAt,
            wr.ProcessedByUserId
        )).ToList();
        return Result.Ok<IReadOnlyList<WithdrawalRequestDto>>(dtos);
    }

    private static string MaskAccountNumber(string accountNumber)
    {
        if (string.IsNullOrEmpty(accountNumber) || accountNumber.Length < 4)
            return "****";
        return "****" + accountNumber.Substring(accountNumber.Length - 4);
    }
}

public class GetDriverMissionsQueryHandler : IRequestHandler<GetDriverMissionsQuery, Result<IReadOnlyList<DriverMissionDto>>>
{
    private readonly IDriverWalletRepository _repository;

    public GetDriverMissionsQueryHandler(IDriverWalletRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<DriverMissionDto>>> Handle(GetDriverMissionsQuery request, CancellationToken ct)
    {
        var missions = await _repository.GetMissionsByDriverIdAsync(request.DriverId, request.Status, ct);

        var dtos = missions.Select(m => new DriverMissionDto(
            m.Id,
            m.DriverId,
            m.Title,
            m.Description,
            m.Reward,
            m.Progress,
            m.Target,
            m.Status,
            m.Type,
            m.ExpiresAt,
            m.CompletedAt,
            m.RewardedAt
        )).ToList();

        return Result.Ok<IReadOnlyList<DriverMissionDto>>(dtos);
    }
}

public class RequestWithdrawalCommandHandler : IRequestHandler<RequestWithdrawalCommand, Result<WithdrawalRequestDto>>
{
    private readonly IDriverWalletRepository _repository;
    private readonly ISavedWithdrawalMethodRepository _savedWithdrawalMethodRepository;
    private readonly IPaymentGatewayFactory _gatewayFactory;
    private readonly IDriverOutboxPublisher _outboxPublisher;
    private readonly UserManager<ApplicationUser>? _userManager;
    private readonly DriverWalletOptions _options;
    private readonly ILogger<RequestWithdrawalCommandHandler> _logger;
    private readonly Services.PayMongoWithdrawalService? _payMongoWithdrawals;

    public RequestWithdrawalCommandHandler(
        IDriverWalletRepository repository,
        ISavedWithdrawalMethodRepository savedWithdrawalMethodRepository,
        IPaymentGatewayFactory gatewayFactory,
        IDriverOutboxPublisher outboxPublisher,
        IOptions<DriverWalletOptions> options,
        ILogger<RequestWithdrawalCommandHandler> logger,
        UserManager<ApplicationUser>? userManager = null,
        Services.PayMongoWithdrawalService? payMongoWithdrawals = null)
    {
        _payMongoWithdrawals = payMongoWithdrawals;
        _repository = repository;
        _savedWithdrawalMethodRepository = savedWithdrawalMethodRepository;
        _gatewayFactory = gatewayFactory;
        _outboxPublisher = outboxPublisher;
        _options = options.Value;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<WithdrawalRequestDto>> Handle(RequestWithdrawalCommand request, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogInformation(
            "[WITHDRAWAL] [REQUEST] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Starting - DriverId: {DriverId}, Amount: {Amount}, SavedWithdrawalMethodId: {SavedWithdrawalMethodId}, HasBankDetails: {HasBankDetails}",
            startTime, request.DriverId, request.Amount, request.SavedWithdrawalMethodId?.ToString() ?? "null", 
            !string.IsNullOrEmpty(request.BankAccountNumber));
        
        var wallet = await _repository.GetWalletByDriverIdAsync(request.DriverId, ct);
        if (wallet == null)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger.LogWarning(
                "[WITHDRAWAL] [REQUEST] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Wallet Not Found - DriverId: {DriverId}, Duration: {Duration}ms",
                DateTime.UtcNow, request.DriverId, duration);
            return Result.NotFound<WithdrawalRequestDto>("Wallet not found");
        }

        // Idempotency: return existing withdrawal if same key was already used
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim();
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var existing = await _repository.GetWithdrawalByDriverAndIdempotencyKeyAsync(request.DriverId, idempotencyKey, ct);
            if (existing != null)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogInformation(
                    "[WITHDRAWAL] [REQUEST] [IDEMPOTENCY] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Returning existing - DriverId: {DriverId}, WithdrawalId: {WithdrawalId}, Amount: {Amount}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.DriverId, existing.Id, existing.Amount, duration);
                var dto = new WithdrawalRequestDto(
                    existing.Id,
                    existing.DriverId,
                    existing.WalletId,
                    existing.Amount,
                    existing.Status,
                    MaskWithdrawalAccountNumber(existing.BankAccountNumber),
                    existing.BankName,
                    existing.AccountHolderName,
                    existing.RejectionReason,
                    existing.RequestedAt,
                    existing.ProcessedAt,
                    existing.ProcessedByUserId
                );
                return Result.Ok(dto);
            }
        }

        if (_options.MaxWithdrawalAmount.HasValue && request.Amount > _options.MaxWithdrawalAmount.Value)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger.LogWarning(
                "[WITHDRAWAL] [REQUEST] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Exceeds max - DriverId: {DriverId}, Amount: {Amount}, Max: {Max}, Duration: {Duration}ms",
                DateTime.UtcNow, request.DriverId, request.Amount, _options.MaxWithdrawalAmount.Value, duration);
            return Result.Fail<WithdrawalRequestDto>($"Maximum withdrawal amount is {_options.MaxWithdrawalAmount.Value}");
        }

        // --- PayMongo child wallet (issue #91, phase 3) ---
        // A migrated driver's balance lives at PayMongo, not here, so everything below this point
        // is inapplicable to them: the integrity check compares a local balance that is no longer
        // authoritative, and the reserve-then-send machinery guards a custody role we no longer
        // hold. Handles() is false for every unmigrated driver and whenever the flag is off, so
        // the original path is reached byte-for-byte as before.
        // A driver whose balance is at PayMongo must NEVER be paid down the platform path, whatever
        // the flags say. That path reserves against the local mirror and disburses from the
        // PLATFORM wallet - so with earnings already pushed to their child wallet the driver would
        // be paid TWICE: once into their own wallet, which they keep, and again out of our funds.
        //
        // Refusing here rather than falling through means the two flags cannot be set to a
        // combination that loses money. Turning withdrawals off pauses them; it never reroutes them.
        if (wallet.UsesPayMongoWallet && (_payMongoWithdrawals is null || !_payMongoWithdrawals.Handles(wallet)))
        {
            _logger.LogWarning(
                "[WITHDRAWAL] [REQUEST] Driver {DriverId} holds their balance at PayMongo but the "
                + "child-wallet withdrawal path is disabled; refusing rather than paying from the platform wallet.",
                request.DriverId);
            return Result.Fail<WithdrawalRequestDto>(
                "Withdrawals are temporarily paused for your wallet. Your earnings are safe - please try again later.");
        }

        if (_payMongoWithdrawals is not null && _payMongoWithdrawals.Handles(wallet))
        {
            if (request.DestinationType == WithdrawalDestinationType.QrPh)
                return Result.Fail<WithdrawalRequestDto>(
                    "QR Ph withdrawals aren't available from your new wallet yet. Use a bank or e-wallet.");

            var destination = await ResolveChildWalletDestinationAsync(request, ct);
            if (destination is null)
                return Result.Fail<WithdrawalRequestDto>("Please select a bank or e-wallet to withdraw to.");

            var payout = await _payMongoWithdrawals.WithdrawAsync(
                wallet, request.Amount, destination.Value.BankCode,
                destination.Value.AccountNumber, destination.Value.AccountHolderName,
                idempotencyKey, ct);

            if (!payout.IsSuccess)
                return Result.Fail<WithdrawalRequestDto>(payout.Error);

            var w = payout.Value!;
            return Result.Ok(new WithdrawalRequestDto(
                w.Id, w.DriverId, w.WalletId, w.Amount, w.Status,
                MaskWithdrawalAccountNumber(w.BankAccountNumber), w.BankName, w.AccountHolderName,
                w.RejectionReason, w.RequestedAt, w.ProcessedAt, w.ProcessedByUserId));
        }

        // --- Integrity Check ---
        var integrityCheckStart = DateTime.UtcNow;
        var calculatedBalance = await _repository.GetCalculatedPersonalBalanceAsync(wallet.Id, ct);
        var integrityCheckDuration = (DateTime.UtcNow - integrityCheckStart).TotalMilliseconds;
        
        if (Math.Abs(calculatedBalance - wallet.Balance) > 0.01m)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger.LogCritical(
                "[WITHDRAWAL] [REQUEST] [INTEGRITY_CHECK] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Mismatch - DriverId: {DriverId}, DB Balance: {DbBalance}, Calculated: {CalcBalance}, Duration: {Duration}ms",
                DateTime.UtcNow, wallet.DriverId, wallet.Balance, calculatedBalance, duration);
            return Result.Fail<WithdrawalRequestDto>("Wallet integrity check failed. Please contact support.");
        }

        if (request.Amount > calculatedBalance)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger.LogWarning(
                "[WITHDRAWAL] [REQUEST] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Insufficient Funds - DriverId: {DriverId}, Amount: {Amount}, Balance: {Balance}, Duration: {Duration}ms",
                DateTime.UtcNow, request.DriverId, request.Amount, calculatedBalance, duration);
            return Result.Fail<WithdrawalRequestDto>("Insufficient funds (verified).");
        }
        
        _logger.LogInformation(
            "[WITHDRAWAL] [REQUEST] [INTEGRITY_CHECK] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Passed - DriverId: {DriverId}, Balance: {Balance}, Amount: {Amount}, Duration: {Duration}ms",
            DateTime.UtcNow, request.DriverId, calculatedBalance, request.Amount, integrityCheckDuration);
        // -----------------------

        // SECURITY: Get bank details from saved method if provided, otherwise use provided details
        string bankAccountNumber;
        string bankName;
        string accountHolderName;
        string bankCode;
        bool usingSavedMethod = false;

        var isQrWithdrawal = request.DestinationType == WithdrawalDestinationType.QrPh;

        if (isQrWithdrawal)
        {
            // A QR Ph code identifies the receiving account by itself, so there is no
            // account number, bank code or rail to validate here — PayMongo parses the
            // string and reports any missing fields. We never parse it ourselves.
            if (string.IsNullOrWhiteSpace(request.QrString))
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogWarning(
                    "[WITHDRAWAL] [REQUEST] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Missing QR String - DriverId: {DriverId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.DriverId, duration);
                return Result.Fail<WithdrawalRequestDto>("Scan a QR Ph code to withdraw this way.");
            }

            // The bank columns are display text on a QR withdrawal; the account number is
            // required and encrypted by the mapping, so it carries the reference instead.
            bankAccountNumber = "QR";
            bankName = "QR Ph";
            accountHolderName = request.AccountHolderName ?? "QR Ph recipient";
            bankCode = "QRPH";
        }
        else if (request.SavedWithdrawalMethodId.HasValue)
        {
            // Use saved withdrawal method
            var savedMethodStart = DateTime.UtcNow;
            var savedMethod = await _savedWithdrawalMethodRepository.GetByIdAsync(request.SavedWithdrawalMethodId.Value, ct);
            
            if (savedMethod == null)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogWarning(
                    "[WITHDRAWAL] [REQUEST] [SAVED_METHOD] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Not Found - DriverId: {DriverId}, SavedWithdrawalMethodId: {SavedWithdrawalMethodId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.DriverId, request.SavedWithdrawalMethodId.Value, duration);
                return Result.Fail<WithdrawalRequestDto>("Saved withdrawal method not found");
            }

            // SECURITY: Verify ownership
            if (savedMethod.DriverId != request.DriverId)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogWarning(
                    "[WITHDRAWAL] [REQUEST] [SAVED_METHOD] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Access Denied - DriverId: {DriverId}, SavedWithdrawalMethodId: {SavedWithdrawalMethodId}, OwnerDriverId: {OwnerDriverId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.DriverId, request.SavedWithdrawalMethodId.Value, savedMethod.DriverId, duration);
                return Result.Fail<WithdrawalRequestDto>("Access denied");
            }

            if (!savedMethod.IsActive)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogWarning(
                    "[WITHDRAWAL] [REQUEST] [SAVED_METHOD] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Inactive - DriverId: {DriverId}, SavedWithdrawalMethodId: {SavedWithdrawalMethodId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.DriverId, request.SavedWithdrawalMethodId.Value, duration);
                return Result.Fail<WithdrawalRequestDto>("Saved withdrawal method is inactive");
            }

            bankAccountNumber = savedMethod.AccountNumber; // Already decrypted by repository
            bankName = savedMethod.BankName;
            accountHolderName = savedMethod.AccountHolderName;
            bankCode = savedMethod.BankCode;
            usingSavedMethod = true;

            // Mark as used
            savedMethod.MarkAsUsed();
            await _savedWithdrawalMethodRepository.SaveChangesAsync(ct);
            
            var savedMethodRetrievalDuration = (DateTime.UtcNow - savedMethodStart).TotalMilliseconds;
            _logger.LogInformation(
                "[WITHDRAWAL] [REQUEST] [SAVED_METHOD] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Using Saved Method - DriverId: {DriverId}, SavedWithdrawalMethodId: {SavedWithdrawalMethodId}, BankName: {BankName}, BankCode: {BankCode}, MaskedAccount: {MaskedAccount}, Duration: {Duration}ms",
                DateTime.UtcNow, request.DriverId, request.SavedWithdrawalMethodId.Value, bankName, bankCode, savedMethod.GetMaskedAccountNumber(), savedMethodRetrievalDuration);
        }
        else
        {
            // Use provided bank details
            if (string.IsNullOrWhiteSpace(request.BankAccountNumber) || 
                string.IsNullOrWhiteSpace(request.BankName) || 
                string.IsNullOrWhiteSpace(request.AccountHolderName))
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogWarning(
                    "[WITHDRAWAL] [REQUEST] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Missing Bank Details - DriverId: {DriverId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.DriverId, duration);
                return Result.Fail<WithdrawalRequestDto>("Bank account details are required when not using a saved withdrawal method");
            }

            bankAccountNumber = request.BankAccountNumber;
            bankName = request.BankName;
            accountHolderName = request.AccountHolderName;
            // The app picks from GET /api/payments/banks and sends the catalog code. App
            // builds predating the picker send no code, so the bank name is still tried as
            // one — it resolves for the friendly codes the catalog already knows (BPI, GCASH…).
            bankCode = string.IsNullOrWhiteSpace(request.BankCode) ? request.BankName : request.BankCode;

            _logger.LogInformation(
                "[WITHDRAWAL] [REQUEST] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Using Provided Bank Details - DriverId: {DriverId}, BankName: {BankName}, BankCode: {BankCode}",
                DateTime.UtcNow, request.DriverId, bankName, bankCode);
        }

        // Validate the destination before the wallet is touched. Left to the gateway this
        // throws mid-payout and reaches the driver as "temporarily unavailable", which is
        // both wrong and unactionable — the bank simply isn't one we can pay out to.
        PhBank? destinationBank = null;
        if (!isQrWithdrawal)
        {
            if (!PhBankCatalog.TryResolve(bankCode, out var resolved))
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogWarning(
                    "[WITHDRAWAL] [REQUEST] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Unsupported Bank - DriverId: {DriverId}, BankCode: {BankCode}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.DriverId, bankCode, duration);
                return Result.Fail<WithdrawalRequestDto>(
                    $"'{bankName}' isn't supported for withdrawals yet. Please pick a bank or e-wallet from the list.");
            }

            destinationBank = resolved;
        }

        // InstaPay caps at PHP 50,000; a bank that can only receive on InstaPay cannot take
        // more than that however the rail is chosen downstream. QR Ph settles on InstaPay,
        // so the same ceiling applies without a catalog entry to read it from.
        var destinationLimit = destinationBank?.MaxAmount ?? PhBank.InstapayLimit;
        if (request.Amount > destinationLimit)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger.LogWarning(
                "[WITHDRAWAL] [REQUEST] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Above Rail Limit - DriverId: {DriverId}, BankCode: {BankCode}, Amount: {Amount}, Limit: {Limit}, Duration: {Duration}ms",
                DateTime.UtcNow, request.DriverId, bankCode, request.Amount, destinationLimit, duration);
            return Result.Fail<WithdrawalRequestDto>(
                $"{bankName} can only receive up to {destinationLimit:C} per transfer. Please withdraw a smaller amount.");
        }

        // Generate withdrawal id up front so we can use it as the provider reference_id before persisting
        var withdrawalId = Guid.NewGuid();
        var referenceId = $"WD-{withdrawalId}";

        // New withdrawals go to the active gateway; existing records route by their stored provider.
        var gateway = _gatewayFactory.GetActive();

        try
        {
            // --- Reserve the funds BEFORE calling the provider ---
            //
            // The old order (provider first, debit after) let two devices both pass the
            // balance check on their own snapshots and both get a real transfer executed,
            // with only one surviving the save. Reserving first moves the wallet's xmin
            // check ahead of the irreversible act: the loser now fails here, having sent
            // nothing. See issue #90.
            wallet.RequestWithdrawal(request.Amount);
            wallet.UpdateBankDetails(bankAccountNumber, bankName, accountHolderName);

            // autoApprove:false — the withdrawal is only Approved once the provider accepts it.
            var withdrawalRequest = new WithdrawalRequest(
                withdrawalId,
                request.DriverId,
                wallet.Id,
                request.Amount,
                bankAccountNumber,
                bankName,
                accountHolderName,
                autoApprove: false,
                idempotencyKey: idempotencyKey,
                destinationType: request.DestinationType
            );

            var transaction = new WalletTransaction(
                wallet.Id,
                WalletTransactionType.Withdrawal,
                WalletBucket.Personal,
                request.Amount,
                WalletTransactionStatus.Pending,
                $"Withdrawal request: {request.Amount:C}",
                null,
                withdrawalRequest.Id
            );

            try
            {
                await _repository.SaveWithdrawalReservationAsync(withdrawalRequest, wallet, transaction, ct);
            }
            catch (Exception saveEx) when (saveEx is DbUpdateConcurrencyException or DbUpdateException)
            {
                // Either another withdrawal for this wallet won the xmin race, or the same
                // idempotency key was submitted twice concurrently and hit the unique index.
                // Both mean this request reserved nothing and sent nothing — there is no money
                // to unwind, so the only job left is to say so in words a driver can act on.
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogWarning(
                    "[WITHDRAWAL] [REQUEST] [CONCURRENCY] Lost the race for wallet {WalletId} - DriverId: {DriverId}, Amount: {Amount}, Duration: {Duration}ms",
                    wallet.Id, request.DriverId, request.Amount, duration);
                return Result.Fail<WithdrawalRequestDto>(
                    "Another withdrawal is already being processed. Please wait for it to finish before starting a new one.");
            }

            _logger.LogInformation(
                "[WITHDRAWAL] [REQUEST] [RESERVED] WithdrawalId: {WithdrawalId}, DriverId: {DriverId}, Amount: {Amount} held in PendingPayout",
                withdrawalId, request.DriverId, request.Amount);

            // --- Funds are held; now call the provider ---
            var disbursementStart = DateTime.UtcNow;
            _logger.LogInformation(
                "[WITHDRAWAL] [REQUEST] [PAYOUT] Creating - Gateway: {Gateway}, WithdrawalId: {WithdrawalId}, DriverId: {DriverId}, Amount: {Amount}, Destination: {Destination}, BankCode: {BankCode}, UsingSavedMethod: {UsingSavedMethod}",
                gateway.ProviderName, withdrawalId, request.DriverId, request.Amount, request.DestinationType, bankCode, usingSavedMethod);

            GatewayDisbursement disbursement;
            try
            {
                disbursement = isQrWithdrawal
                    ? await gateway.ExecuteQrDisbursementAsync(new ExecuteGatewayQrDisbursementRequest(
                        ReferenceId: referenceId,
                        Amount: request.Amount,
                        QrString: request.QrString!,
                        IdempotencyKey: idempotencyKey), ct)
                    : await gateway.CreateDisbursementAsync(new CreateGatewayDisbursementRequest(
                        ReferenceId: referenceId,
                        Amount: request.Amount,
                        BankCode: bankCode,
                        AccountHolderName: accountHolderName,
                        AccountNumber: bankAccountNumber,
                        Description: $"BEE Driver Payout - {request.Amount:C}",
                        IdempotencyKey: idempotencyKey), ct);
            }
            catch (Exception disbEx)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogError(disbEx,
                    "[WITHDRAWAL] [REQUEST] [PAYOUT] Failed - Gateway: {Gateway}, WithdrawalId: {WithdrawalId}, DriverId: {DriverId}, Amount: {Amount}, Duration: {Duration}ms",
                    gateway.ProviderName, withdrawalId, request.DriverId, request.Amount, duration);

                // Only refund when the provider definitely did NOT take the transfer. A
                // timeout or 5xx means we do not know: the transfer may exist, and refunding
                // would let the driver withdraw the same money twice. Those are left Pending
                // with their reference for WithdrawalReconciliationService to resolve.
                var definitelyNotSent = disbEx switch
                {
                    PayoutsNotConfiguredException => true,   // never left the process
                    ArgumentException => true,               // rejected before the HTTP call
                    NotSupportedException => true,           // gateway has no such rail
                    PaymentGatewayException { StatusCode: >= 400 and < 500 } => true,
                    _ => false
                };

                if (definitelyNotSent)
                {
                    await RefundReservationAsync(withdrawalRequest, wallet, transaction,
                        $"Provider rejected the payout: {disbEx.Message}", ct);
                }
                else
                {
                    _logger.LogWarning(
                        "[WITHDRAWAL] [REQUEST] [PAYOUT] Ambiguous outcome for {WithdrawalId} (reference {ReferenceId}); funds stay reserved for reconciliation",
                        withdrawalId, referenceId);
                }

                return Result.Fail<WithdrawalRequestDto>(disbEx switch
                {
                    PayoutsNotConfiguredException =>
                        "Payouts are temporarily disabled. Please contact support.",
                    NotSupportedException =>
                        "That withdrawal method isn't available right now. Please use a bank transfer.",
                    ArgumentException =>
                        "That bank isn't supported for withdrawals yet. Please pick another from the list.",
                    PaymentGatewayException { StatusCode: 400 } =>
                        "Payout request was rejected. Please check bank details and try again.",
                    // 401/403 means the key or the wallet is not entitled to transfers —
                    // an operator problem, never something the driver can retry past.
                    PaymentGatewayException { StatusCode: 401 or 403 } =>
                        "Payouts are temporarily disabled. Please contact support.",
                    _ => "Payout service is temporarily unavailable. We're confirming this withdrawal and will update it shortly."
                });
            }

            var disbursementDuration = (DateTime.UtcNow - disbursementStart).TotalMilliseconds;
            _logger.LogInformation(
                "[WITHDRAWAL] [REQUEST] [PAYOUT] Accepted - Gateway: {Gateway}, WithdrawalId: {WithdrawalId}, PayoutId: {PayoutId}, Status: {Status}, Duration: {Duration}ms",
                gateway.ProviderName, withdrawalId, disbursement.ProviderDisbursementId, disbursement.RawStatus, disbursementDuration);

            // Provider accepted: promote the reservation to Approved and record the payout id.
            withdrawalRequest.SetProviderDisbursement(gateway.ProviderName, disbursement.ProviderDisbursementId);
            if (isQrWithdrawal)
                withdrawalRequest.SetQrId(disbursement.ProviderDisbursementId);
            withdrawalRequest.AutoApprove();

            // Resolve driver email/name for receipt (optional; receipt email sent by consumer when DriverEmail is set)
            string? driverEmail = null;
            string? driverName = null;
            if (_userManager != null)
            {
                var driverUser = await _userManager.FindByIdAsync(request.DriverId.ToString());
                if (!string.IsNullOrWhiteSpace(driverUser?.Email))
                {
                    driverEmail = driverUser.Email;
                    driverName = string.IsNullOrWhiteSpace(driverUser.FullName) ? driverEmail.Split('@')[0] : driverUser.FullName;
                }
            }

            var maskedAccount = MaskWithdrawalAccountNumber(withdrawalRequest.BankAccountNumber);

            // Add outbox message to DbContext tracker BEFORE the final SaveChanges
            // so it's committed atomically with the wallet update
            _outboxPublisher.Publish(new WithdrawalRequestedEvent(
                withdrawalRequest.Id,
                request.DriverId,
                withdrawalRequest.WalletId,
                request.Amount,
                "PHP",
                withdrawalRequest.RequestedAt,
                idempotencyKey,
                DriverEmail: driverEmail,
                DriverName: driverName,
                ProviderPayoutId: disbursement.ProviderDisbursementId,
                MaskedAccountNumber: maskedAccount,
                BankName: bankName ?? ""
            ));

            // Commits the promotion to Approved, the provider id, and the outbox message in
            // one SaveChanges. The wallet was already debited by the reservation and is
            // unchanged here, so it is deliberately not re-written.
            await _repository.UpdateWithdrawalRequestAsync(withdrawalRequest, ct);

            var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger.LogInformation(
                "[WITHDRAWAL] [REQUEST] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Success - WithdrawalId: {WithdrawalId}, DriverId: {DriverId}, Amount: {Amount}, BankName: {BankName}, UsingSavedMethod: {UsingSavedMethod}, TotalDuration: {TotalDuration}ms",
                DateTime.UtcNow, withdrawalRequest.Id, request.DriverId, request.Amount, bankName, usingSavedMethod, totalDuration);

            var dto = new WithdrawalRequestDto(
                withdrawalRequest.Id,
                withdrawalRequest.DriverId,
                withdrawalRequest.WalletId,
                withdrawalRequest.Amount,
                withdrawalRequest.Status,
                MaskWithdrawalAccountNumber(withdrawalRequest.BankAccountNumber),
                withdrawalRequest.BankName,
                withdrawalRequest.AccountHolderName,
                withdrawalRequest.RejectionReason,
                withdrawalRequest.RequestedAt,
                withdrawalRequest.ProcessedAt,
                withdrawalRequest.ProcessedByUserId
            );

            return Result.Ok(dto);
        }
        catch (Exception ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger.LogError(ex,
                "[WITHDRAWAL] [REQUEST] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Error - DriverId: {DriverId}, Amount: {Amount}, SavedWithdrawalMethodId: {SavedWithdrawalMethodId}, Duration: {Duration}ms, Exception: {Exception}, StackTrace: {StackTrace}",
                DateTime.UtcNow, request.DriverId, request.Amount, request.SavedWithdrawalMethodId?.ToString() ?? "null", duration, ex.Message, ex.StackTrace);
            return Result.Fail<WithdrawalRequestDto>(ex.Message);
        }
    }

    /// <summary>
    /// Unwinds a reservation whose payout definitely never left. Returns the held funds to
    /// the driver's balance and marks both the withdrawal and its ledger row Failed, in one
    /// SaveChanges. Never call this for an ambiguous provider outcome — see issue #90.
    /// </summary>
    private async Task RefundReservationAsync(
        WithdrawalRequest withdrawalRequest,
        DriverWallet wallet,
        WalletTransaction transaction,
        string reason,
        CancellationToken ct)
    {
        try
        {
            wallet.RejectWithdrawal(withdrawalRequest.Amount);
            withdrawalRequest.MarkAsFailed(reason);
            transaction.MarkAsFailed();
            await _repository.SaveWithdrawalDisbursementResultAsync(withdrawalRequest, wallet, transaction, ct);

            _logger.LogInformation(
                "[WITHDRAWAL] [REQUEST] [REFUNDED] Reservation {WithdrawalId} released back to balance - Amount: {Amount}",
                withdrawalRequest.Id, withdrawalRequest.Amount);
        }
        catch (Exception refundEx)
        {
            // The reservation stays Pending and reconciliation will pick it up. Loud, because
            // until then the driver's funds are held for a payout that was never sent.
            _logger.LogError(refundEx,
                "[WITHDRAWAL] [REQUEST] [REFUND_FAILED] Could not release reservation {WithdrawalId} for driver {DriverId}, Amount: {Amount}. Funds remain in PendingPayout.",
                withdrawalRequest.Id, withdrawalRequest.DriverId, withdrawalRequest.Amount);
        }
    }

    /// <summary>
    /// Resolves where a child-wallet withdrawal is going: a saved method if one was named,
    /// otherwise the details supplied on the request. Ownership is verified, because a saved method
    /// id is client-supplied and pointing it at someone else's account would send this driver's
    /// money to a stranger.
    /// </summary>
    private async Task<(string BankCode, string AccountNumber, string AccountHolderName)?>
        ResolveChildWalletDestinationAsync(RequestWithdrawalCommand request, CancellationToken ct)
    {
        if (request.SavedWithdrawalMethodId.HasValue)
        {
            var saved = await _savedWithdrawalMethodRepository.GetByIdAsync(request.SavedWithdrawalMethodId.Value, ct);
            if (saved is null || saved.DriverId != request.DriverId) return null;
            return (saved.BankCode ?? saved.BankName, saved.AccountNumber, saved.AccountHolderName);
        }

        if (string.IsNullOrWhiteSpace(request.BankAccountNumber) ||
            string.IsNullOrWhiteSpace(request.AccountHolderName))
            return null;

        var code = string.IsNullOrWhiteSpace(request.BankCode) ? request.BankName : request.BankCode;
        if (string.IsNullOrWhiteSpace(code)) return null;

        return (code!, request.BankAccountNumber!, request.AccountHolderName!);
    }

    private static string MaskWithdrawalAccountNumber(string accountNumber)
    {
        if (string.IsNullOrEmpty(accountNumber) || accountNumber.Length < 4)
            return "****";
        return "****" + accountNumber.Substring(accountNumber.Length - 4);
    }
}

public class ClaimMissionRewardCommandHandler : IRequestHandler<ClaimMissionRewardCommand, Result<DriverMissionDto>>
{
    private readonly IDriverWalletRepository _repository;
    private readonly IPublishEndpoint? _publishEndpoint;
    private readonly ILogger<ClaimMissionRewardCommandHandler>? _logger;

    public ClaimMissionRewardCommandHandler(
        IDriverWalletRepository repository,
        IPublishEndpoint? publishEndpoint = null,
        ILogger<ClaimMissionRewardCommandHandler>? logger = null)
    {
        _repository = repository;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task<Result<DriverMissionDto>> Handle(ClaimMissionRewardCommand request, CancellationToken ct)
    {
        var mission = await _repository.GetMissionByIdAsync(request.MissionId, ct);
        if (mission == null)
        {
            return Result.NotFound<DriverMissionDto>("Mission not found");
        }

        if (mission.DriverId != request.DriverId)
        {
            return Result.Fail<DriverMissionDto>("Mission does not belong to this driver");
        }

        try
        {
            mission.ClaimReward();

            // Add reward to wallet
            var wallet = await _repository.GetWalletByDriverIdAsync(request.DriverId, ct);
            if (wallet == null)
            {
                wallet = new DriverWallet(request.DriverId);
                await _repository.CreateWalletAsync(wallet, ct);
            }

            wallet.AddEarning(mission.Reward);

            // Create transaction
            var transaction = new WalletTransaction(
                wallet.Id,
                WalletTransactionType.Earning,
                WalletBucket.Personal,
                mission.Reward,
                WalletTransactionStatus.Completed,
                $"Mission reward: {mission.Title}",
                null,
                null
            );

            await _repository.CreateTransactionAsync(transaction, ct);
            await _repository.UpdateWalletAsync(wallet, ct);

            // Notify the back-office backend (webhook). Mission completion is
            // observable at reward claim — the only lifecycle point wired today.
            if (_publishEndpoint != null)
            {
                try
                {
                    await _publishEndpoint.Publish(new MissionCompletedEvent(
                        mission.Id, mission.DriverId, mission.Title, mission.Reward,
                        mission.CompletedAt ?? DateTime.UtcNow), ct);
                }
                catch (Exception publishEx)
                {
                    _logger?.LogError(publishEx, "Failed to publish MissionCompletedEvent for mission {MissionId}", mission.Id);
                }
            }

            var dto = new DriverMissionDto(
                mission.Id,
                mission.DriverId,
                mission.Title,
                mission.Description,
                mission.Reward,
                mission.Progress,
                mission.Target,
                mission.Status,
                mission.Type,
                mission.ExpiresAt,
                mission.CompletedAt,
                mission.RewardedAt
            );

            return Result.Ok(dto);
        }
        catch (Exception ex)
        {
            return Result.Fail<DriverMissionDto>(ex.Message);
        }
    }
}

public class CreateDriverTopUpCommandHandler : IRequestHandler<CreateDriverTopUpCommand, Result<DriverTopUpDto>>
{
    private readonly IDriverWalletRepository _repository;
    private readonly IPaymentGatewayFactory _gatewayFactory;
    private readonly DriverWalletOptions _options;

    public CreateDriverTopUpCommandHandler(
        IDriverWalletRepository repository,
        IPaymentGatewayFactory gatewayFactory,
        IOptions<DriverWalletOptions> options)
    {
        _repository = repository;
        _gatewayFactory = gatewayFactory;
        _options = options.Value;
    }

    public async Task<Result<DriverTopUpDto>> Handle(CreateDriverTopUpCommand request, CancellationToken ct)
    {
        if (request.Amount < _options.MinTopUpAmount)
            return Result.Fail<DriverTopUpDto>($"Minimum top-up amount is {_options.MinTopUpAmount}");
        if (_options.MaxTopUpAmount.HasValue && request.Amount > _options.MaxTopUpAmount.Value)
            return Result.Fail<DriverTopUpDto>($"Maximum top-up amount is {_options.MaxTopUpAmount.Value}");

        var wallet = await _repository.GetWalletByDriverIdAsync(request.DriverId, ct);
        if (wallet == null)
            wallet = await _repository.CreateWalletAsync(new DriverWallet(request.DriverId), ct);

        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim();
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var existing = await _repository.GetTopUpByDriverAndIdempotencyKeyAsync(request.DriverId, idempotencyKey, ct);
            if (existing != null)
                return Result.Ok(DriverWalletMapper.ToTopUpDto(existing));
        }

        var externalId = $"DRVTOPUP-{Guid.NewGuid():N}";
        var topUp = new DriverTopUp(request.DriverId, wallet.Id, request.Amount, externalId, idempotencyKey);
        await _repository.CreateTopUpAsync(topUp, ct);

        var payerEmail = string.IsNullOrWhiteSpace(request.PayerEmail)
            ? $"driver+{request.DriverId:N}@mybeeapp.local"
            : request.PayerEmail.Trim();

        var description = string.IsNullOrWhiteSpace(request.Description)
            ? "Driver top-up wallet funding"
            : request.Description.Trim();

        var invoiceDuration = _options.TopUpInvoiceDurationSeconds > 0
            ? _options.TopUpInvoiceDurationSeconds
            : 86400;

        // New top-ups go to the active gateway; reconciliation/webhooks for
        // existing top-ups route by the provider stored on the record.
        var gateway = _gatewayFactory.GetActive();

        try
        {
            var session = await gateway.CreateCheckoutAsync(new CreateCheckoutRequest(
                ReferenceId: externalId,
                Amount: request.Amount,
                PayerEmail: payerEmail,
                Description: description,
                ExpirySeconds: invoiceDuration), ct);

            topUp.SetProviderCheckout(gateway.ProviderName, session.ProviderPaymentId, session.CheckoutUrl, null);
            await _repository.SaveChangesAsync(ct);

            return Result.Ok(DriverWalletMapper.ToTopUpDto(topUp));
        }
        catch (Exception ex)
        {
            topUp.MarkAsFailed($"Invoice creation failed: {ex.Message}");
            await _repository.SaveChangesAsync(ct);
            return Result.Fail<DriverTopUpDto>("Failed to create payment invoice for top-up");
        }
    }
}

public class GetDriverTopUpQueryHandler : IRequestHandler<GetDriverTopUpQuery, Result<DriverTopUpDto>>
{
    private readonly IDriverWalletRepository _repository;

    public GetDriverTopUpQueryHandler(IDriverWalletRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<DriverTopUpDto>> Handle(GetDriverTopUpQuery request, CancellationToken ct)
    {
        var topUp = await _repository.GetTopUpByIdAsync(request.TopUpId, ct);
        if (topUp == null || topUp.DriverId != request.DriverId)
            return Result.NotFound<DriverTopUpDto>("Top-up not found");

        return Result.Ok(DriverWalletMapper.ToTopUpDto(topUp));
    }
}

public class GetDriverTopUpHistoryQueryHandler : IRequestHandler<GetDriverTopUpHistoryQuery, Result<IReadOnlyList<DriverTopUpDto>>>
{
    private readonly IDriverWalletRepository _repository;

    public GetDriverTopUpHistoryQueryHandler(IDriverWalletRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<DriverTopUpDto>>> Handle(GetDriverTopUpHistoryQuery request, CancellationToken ct)
    {
        var topUps = await _repository.GetTopUpsByDriverIdAsync(request.DriverId, ct);
        return Result.Ok<IReadOnlyList<DriverTopUpDto>>(topUps.Select(DriverWalletMapper.ToTopUpDto).ToList());
    }
}

public class CancelDriverTopUpCommandHandler : IRequestHandler<CancelDriverTopUpCommand, Result<DriverTopUpDto>>
{
    private readonly IDriverWalletRepository _repository;

    public CancelDriverTopUpCommandHandler(IDriverWalletRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<DriverTopUpDto>> Handle(CancelDriverTopUpCommand request, CancellationToken ct)
    {
        var topUp = await _repository.GetTopUpByIdAsync(request.TopUpId, ct);
        if (topUp == null || topUp.DriverId != request.DriverId)
            return Result.NotFound<DriverTopUpDto>("Top-up not found");

        try
        {
            topUp.MarkAsCancelled(request.Reason);
            await _repository.SaveChangesAsync(ct);
            return Result.Ok(DriverWalletMapper.ToTopUpDto(topUp));
        }
        catch (InvalidOperationException ex)
        {
            return Result.Fail<DriverTopUpDto>(ex.Message);
        }
    }
}

/// <summary>
/// Moves money between a driver's two buckets (issue #95).
/// <para>
/// For a driver whose balance still lives locally this is pure bookkeeping: two numbers on one row.
/// For a <b>migrated</b> driver it is not. Their BeePay balance is real money in their own PayMongo
/// child wallet, while the Cash Wallet float is money the platform holds and draws its commission
/// from. Moving the local numbers alone would let a driver mint float backed by money sitting in a
/// wallet the platform cannot touch — and then withdraw it — while our books counted it as
/// collateral for cash jobs.
/// </para>
/// <para>
/// So for a migrated driver each direction carries a matching parent↔child transfer. Those are
/// in-network at PayMongo: settled immediately and, measured on live, <c>fee: 0</c> with
/// <c>provider: "paymongo"</c> — no InstaPay hop, nothing for the driver to wait for.
/// </para>
/// </summary>
public class TransferWalletBalanceCommandHandler : IRequestHandler<TransferWalletBalanceCommand, Result<DriverWalletDto>>
{
    private readonly IDriverWalletRepository _repository;
    private readonly DriverWalletOptions _options;
    private readonly IPayMongoAccountsClient? _payMongoAccounts;
    private readonly ILogger<TransferWalletBalanceCommandHandler>? _logger;

    public TransferWalletBalanceCommandHandler(
        IDriverWalletRepository repository,
        IOptions<DriverWalletOptions> options,
        IPayMongoAccountsClient? payMongoAccounts = null,
        ILogger<TransferWalletBalanceCommandHandler>? logger = null)
    {
        _repository = repository;
        _options = options.Value;
        _payMongoAccounts = payMongoAccounts;
        _logger = logger;
    }

    public async Task<Result<DriverWalletDto>> Handle(TransferWalletBalanceCommand request, CancellationToken ct)
    {
        if (request.From == request.To)
            return Result.Fail<DriverWalletDto>("Source and destination wallets must be different");

        if (request.Amount <= 0)
            return Result.Fail<DriverWalletDto>("Amount must be positive");

        var wallet = await _repository.GetWalletByDriverIdAsync(request.DriverId, ct);
        if (wallet == null)
            wallet = await _repository.CreateWalletAsync(new DriverWallet(request.DriverId), ct);

        if (request.From != WalletBucket.Personal && request.From != WalletBucket.TopUp)
            return Result.Fail<DriverWalletDto>("Unsupported transfer direction");

        // Real money only moves for a migrated driver whose earnings actually live at PayMongo.
        // Before phase 2 the BeePay balance is a local number like the float, so the original
        // bookkeeping-only path stays exactly as it was.
        var movesRealMoney =
            _options.PayMongoEarningsPushEnabled &&
            _payMongoAccounts is not null &&
            wallet.UsesPayMongoWallet;

        try
        {
            if (request.From != WalletBucket.Personal && request.From != WalletBucket.TopUp)
                return Result.Fail<DriverWalletDto>("Unsupported transfer direction");
            if (request.To != WalletBucket.Personal && request.To != WalletBucket.TopUp)
                return Result.Fail<DriverWalletDto>("Unsupported transfer direction");

            // The buckets are moved only once the money has. Mutating first and "not saving" on
            // failure is not safe: the wallet is a tracked entity, so the very next SaveChanges in
            // this scope - marking the legs failed does one - would commit a balance move that no
            // transfer backs. The domain methods below still own the rules; this is only about when
            // they are allowed to run.
            var sourceBalance = request.From == WalletBucket.Personal ? wallet.Balance : wallet.TopUpBalance;
            if (sourceBalance < request.Amount)
            {
                return Result.Fail<DriverWalletDto>(request.From == WalletBucket.Personal
                    ? "Insufficient personal wallet balance"
                    : "Insufficient top-up wallet balance");
            }

            void ApplyToBuckets()
            {
                if (request.From == WalletBucket.Personal) wallet.TransferPersonalToTopUp(request.Amount);
                else wallet.TransferTopUpToPersonal(request.Amount);
            }

            var status = movesRealMoney ? WalletTransactionStatus.Pending : WalletTransactionStatus.Completed;

            var outbound = new WalletTransaction(
                wallet.Id,
                WalletTransactionType.WalletTransferOut,
                request.From,
                request.Amount,
                status,
                $"Wallet transfer out ({request.From} -> {request.To})");
            var inbound = new WalletTransaction(
                wallet.Id,
                WalletTransactionType.WalletTransferIn,
                request.To,
                request.Amount,
                status,
                $"Wallet transfer in ({request.From} -> {request.To})");

            if (!movesRealMoney)
            {
                ApplyToBuckets();
                await _repository.SaveWalletTransferAsync(wallet, outbound, inbound, ct);
            }
            else
            {
                // CLAIM BEFORE SENDING, the issue #90 ordering. Both legs are written Pending with
                // the balances untouched, so a crash between here and the provider call leaves a
                // record to reconcile rather than money moved with nothing to explain it.
                await _repository.CreateTransactionAsync(outbound, ct);
                await _repository.CreateTransactionAsync(inbound, ct);

                var reference = $"xfer-{outbound.Id:N}";
                var description = $"BeePay/Cash Wallet transfer ({request.From} -> {request.To})";

                // Parent↔child, both directions in-network: immediate settlement, no InstaPay rail.
                var transfer = request.From == WalletBucket.Personal
                    ? await _payMongoAccounts!.SweepFromChildAsync(
                        wallet.PayMongoAccountId!, wallet.PayMongoAccountNumber!,
                        wallet.AccountHolderName ?? "Driver", request.Amount, reference, description, ct)
                    : await _payMongoAccounts!.TransferToChildAsync(
                        wallet.PayMongoAccountId!, wallet.PayMongoAccountNumber!,
                        wallet.AccountHolderName ?? "Driver", request.Amount, reference, description, ct);

                // Recorded before branching, not just on success. A pending transfer with no id on
                // its legs is unrecoverable: nothing can look it up, the rows stay Pending forever,
                // and HasPendingTransactionsAsync then makes the balance reconciler skip the wallet
                // permanently. The id is the only handle on money that may already have moved.
                outbound.SetProviderPaymentId(transfer.TransferId);
                inbound.SetProviderPaymentId(transfer.TransferId);

                if (transfer.Failed)
                {
                    // Nothing moved at PayMongo, so nothing may move here. Marked failed rather than
                    // deleted so a retry sees a definite outcome instead of an absence.
                    outbound.MarkAsFailed();
                    inbound.MarkAsFailed();
                    await _repository.UpdateTransactionAsync(outbound, ct);
                    await _repository.UpdateTransactionAsync(inbound, ct);

                    _logger?.LogError(
                        "[PAYMONGO] [XFER] {From}->{To} of {Amount} failed for driver {DriverId}: {Reason}",
                        request.From, request.To, request.Amount, request.DriverId,
                        transfer.ProviderErrorMessage ?? "no reason given");

                    return Result.Fail<DriverWalletDto>(
                        "Could not move the money between your wallets. Please try again.");
                }

                if (!transfer.Succeeded)
                {
                    // Failed and Succeeded are not complements — a transfer can sit in some other
                    // state. In-network parent↔child transfers settle immediately, so this is not
                    // expected; treating it as success would move the balances against money that
                    // has not landed, and treating it as failure would write off money that may yet
                    // land. The legs stay Pending, which is both an accurate record and what holds
                    // the balance reconciler off this wallet until it resolves.
                    _logger?.LogWarning(
                        "[PAYMONGO] [XFER] {From}->{To} of {Amount} for driver {DriverId} returned "
                        + "status '{Status}' ({TransferId}); leaving both legs pending.",
                        request.From, request.To, request.Amount, request.DriverId,
                        transfer.Status, transfer.TransferId);

                    await _repository.UpdateTransactionAsync(outbound, ct);
                    await _repository.UpdateTransactionAsync(inbound, ct);

                    return Result.Fail<DriverWalletDto>(
                        "Your transfer is still processing. Your balance will update shortly.");
                }

                // Money has landed; only now do the buckets move.
                ApplyToBuckets();

                outbound.MarkAsCompleted();
                inbound.MarkAsCompleted();

                await _repository.SaveClaimedWalletTransferAsync(wallet, outbound, inbound, ct);

                _logger?.LogInformation(
                    "[PAYMONGO] [XFER] Moved {Amount} {From}->{To} for driver {DriverId} ({TransferId})",
                    request.Amount, request.From, request.To, request.DriverId, transfer.TransferId);
            }

            return Result.Ok(new DriverWalletDto(
                wallet.Id,
                wallet.DriverId,
                wallet.Balance,
                wallet.TopUpBalance,
                wallet.PendingPayout,
                wallet.IsEligibleForCashJobs(await _repository.HasUnpaidCommissionAsync(wallet.Id, ct)),
                wallet.BankAccountNumber,
                wallet.BankName,
                wallet.AccountHolderName,
                wallet.LastUpdatedAt));
        }
        catch (Exception ex)
        {
            return Result.Fail<DriverWalletDto>(ex.Message);
        }
    }
}

public class GetCashJobEligibilityQueryHandler : IRequestHandler<GetCashJobEligibilityQuery, Result<CashJobEligibilityDto>>
{
    private readonly IDriverWalletRepository _repository;
    private readonly DriverWalletOptions _options;

    public GetCashJobEligibilityQueryHandler(IDriverWalletRepository repository, IOptions<DriverWalletOptions> options)
    {
        _repository = repository;
        _options = options.Value;
    }

    public async Task<Result<CashJobEligibilityDto>> Handle(GetCashJobEligibilityQuery request, CancellationToken ct)
    {
        var wallet = await _repository.GetWalletByDriverIdAsync(request.DriverId, ct);
        if (wallet == null)
            wallet = await _repository.CreateWalletAsync(new DriverWallet(request.DriverId), ct);

        var blockThreshold = wallet.CashJobBlockThresholdOverride ?? _options.DefaultCashJobBlockThreshold;
        var negativeLimit = wallet.ResolveTopUpNegativeLimit(_options.DefaultTopUpNegativeLimit);

        return Result.Ok(new CashJobEligibilityDto(
            wallet.IsEligibleForCashJobs(await _repository.HasUnpaidCommissionAsync(wallet.Id, ct)),
            wallet.TopUpBalance,
            blockThreshold,
            negativeLimit));
    }
}

public class ProcessDriverTopUpWebhookCommandHandler : IRequestHandler<ProcessDriverTopUpWebhookCommand, Result>
{
    /// <summary>Driver wallets are denominated in PHP; nothing in the schema carries a per-wallet currency.</summary>
    private const string WalletCurrency = "PHP";

    private readonly IDriverWalletRepository _repository;
    private readonly IDriverTopUpEventBroadcaster? _eventBroadcaster;
    private readonly ILogger<ProcessDriverTopUpWebhookCommandHandler>? _logger;
    private readonly IDriverOutboxPublisher? _outboxPublisher;

    public ProcessDriverTopUpWebhookCommandHandler(
        IDriverWalletRepository repository,
        IDriverTopUpEventBroadcaster? eventBroadcaster = null,
        ILogger<ProcessDriverTopUpWebhookCommandHandler>? logger = null,
        IDriverOutboxPublisher? outboxPublisher = null)
    {
        _repository = repository;
        _eventBroadcaster = eventBroadcaster;
        _logger = logger;
        _outboxPublisher = outboxPublisher;
    }

    public async Task<Result> Handle(ProcessDriverTopUpWebhookCommand request, CancellationToken ct)
    {
        var topUp = await _repository.GetTopUpByProviderPaymentIdAsync(request.Provider, request.ProviderPaymentId, ct);
        if (topUp == null && !string.IsNullOrWhiteSpace(request.ExternalId))
        {
            topUp = await _repository.GetTopUpByExternalIdAsync(request.ExternalId, ct);
        }
        if (topUp == null)
            // NotFound, not Fail: every booking-payment webhook also reaches this handler, so
            // "no such top-up" is the ordinary case and must stay distinguishable from a top-up
            // we found and then refused to credit. Callers log the two at different levels.
            return Result.NotFound("Top-up not found");

        // If we found by external_id and the checkout id was never stored, set it from webhook
        if (string.IsNullOrEmpty(topUp.ProviderPaymentId) && !string.IsNullOrEmpty(request.ProviderPaymentId))
            topUp.SetProviderCheckout(request.Provider, request.ProviderPaymentId, topUp.ProviderCheckoutUrl ?? string.Empty, topUp.ExpiresAt);

        var status = request.Status.ToUpperInvariant();
        if (status is "PAID" or "SETTLED")
        {
            if (topUp.CreditedAt.HasValue)
                return Result.Ok();

            var wallet = await _repository.GetWalletByDriverIdAsync(topUp.DriverId, ct);
            if (wallet == null)
            {
                // The driver paid and there is nowhere to put the money. Never silent.
                BeeMetrics.DriverTopUpsUncredited.Add(1, new KeyValuePair<string, object?>("reason", "no_wallet"));
                _logger?.LogWarning(
                    "[TOPUP] Top-up {TopUpId} for driver {DriverId} was paid but the driver has no wallet; {Amount} is held uncredited.",
                    topUp.Id, topUp.DriverId, topUp.Amount);
                return Result.Fail("Wallet not found");
            }

            // The wallet holds PHP and the top-up amount was quoted in PHP, so crediting a figure
            // the provider charged in something else would credit a face value that means
            // nothing (GitLab #66). Only checked when the provider actually reports a currency:
            // a null is unknown, not verified, and both current gateways are PHP-only, so
            // rejecting on absence would block every credit for no gain.
            if (!string.IsNullOrWhiteSpace(request.Currency)
                && !string.Equals(request.Currency, WalletCurrency, StringComparison.OrdinalIgnoreCase))
            {
                BeeMetrics.DriverTopUpsUncredited.Add(1, new KeyValuePair<string, object?>("reason", "currency_mismatch"));
                _logger?.LogWarning(
                    "[TOPUP] Top-up {TopUpId} (driver {DriverId}, checkout {ProviderPaymentId}) was paid in {Currency}, but the wallet holds {WalletCurrency}; held uncredited pending manual resolution.",
                    topUp.Id, topUp.DriverId, request.ProviderPaymentId, request.Currency, WalletCurrency);
                return Result.Fail($"Currency mismatch. Expected: {WalletCurrency}, Received: {request.Currency}");
            }

            // Validate amount if provided
            if (request.PaidAmount.HasValue && request.PaidAmount.Value != topUp.Amount)
            {
                // Same: the provider has the driver's money and we are declining to credit it.
                // Recoverable through the admin credit endpoint once a human decides the amount.
                BeeMetrics.DriverTopUpsUncredited.Add(1, new KeyValuePair<string, object?>("reason", "amount_mismatch"));
                _logger?.LogWarning(
                    "[TOPUP] Top-up {TopUpId} (driver {DriverId}, checkout {ProviderPaymentId}) was paid {Received} but expected {Expected}; held uncredited pending manual resolution.",
                    topUp.Id, topUp.DriverId, request.ProviderPaymentId, request.PaidAmount, topUp.Amount);
                return Result.Fail($"Payment amount mismatch. Expected: {topUp.Amount}, Received: {request.PaidAmount}");
            }

            topUp.MarkAsPaid(request.PaidAt ?? DateTime.UtcNow);
            wallet.AddTopUp(topUp.Amount);

            // Idempotency keys on ProviderPaymentId, a real column backed by a unique index
            // (GitLab #66). It used to key on this description string, so any reformatting of the
            // wording silently disabled the guard - which had already happened once on the cash
            // path and needed a repair migration. The wording is now display text only.
            var txDescription = request.Provider == "xendit"
                ? $"Top-up via Xendit invoice {request.ProviderPaymentId}"
                : $"Top-up via {request.Provider} checkout {request.ProviderPaymentId}";
            var hasTx = await _repository.HasTransactionForProviderPaymentAsync(
                wallet.Id, request.ProviderPaymentId, WalletTransactionType.TopUp, ct);
            WalletTransaction? newTransaction = null;
            if (!hasTx)
                newTransaction = new WalletTransaction(
                    wallet.Id,
                    WalletTransactionType.TopUp,
                    WalletBucket.TopUp,
                    topUp.Amount,
                    WalletTransactionStatus.Completed,
                    txDescription,
                    providerPaymentId: request.ProviderPaymentId);

            topUp.MarkCredited();

            // Staged before the SaveChanges that commits it, same reasoning as the cash-settlement
            // debit: without this, a real wallet-balance change (real cash received from the
            // driver) had no ledger representation at all.
            _outboxPublisher?.Publish(new DriverTopUpCreditedEvent(
                topUp.Id, topUp.DriverId, wallet.Id, topUp.Amount, request.Provider, DateTime.UtcNow));

            await _repository.SaveTopUpCreditAsync(topUp, wallet, newTransaction, ct);

            if (_eventBroadcaster != null)
            {
                try
                {
                    await _eventBroadcaster.PublishPaidAsync(new DriverTopUpPaidPayload(
                        topUp.Id,
                        topUp.DriverId,
                        topUp.Amount,
                        "Paid",
                        wallet.Balance,
                        wallet.TopUpBalance,
                        topUp.PaidAt ?? DateTime.UtcNow
                    ), ct);
                }
                catch
                {
                    // Don't fail webhook processing if real-time push fails
                }
            }

            return Result.Ok();
        }

        if (status == "EXPIRED")
        {
            topUp.MarkAsExpired();
            await _repository.SaveChangesAsync(ct);
            return Result.Ok();
        }

        if (status == "FAILED")
        {
            topUp.MarkAsFailed("Top-up payment failed");
            await _repository.SaveChangesAsync(ct);
            return Result.Ok();
        }

        return Result.Ok();
    }
}

/// <summary>
/// Operator recovery path for a top-up the automated flow could not settle (GitLab #64):
/// amount mismatch, a missing wallet since created, or a record the expiry timer closed before
/// anything checked the provider.
/// </summary>
/// <remarks>
/// Idempotent through the same CreditedAt guard the webhook path uses, so a double-click cannot
/// credit twice. The ledger row records who did it and why, so the adjustment is auditable
/// without consulting the provider dashboard.
/// </remarks>
public class AdminCreditDriverTopUpCommandHandler : IRequestHandler<AdminCreditDriverTopUpCommand, Result<DriverTopUpDto>>
{
    private readonly IDriverWalletRepository _repository;
    private readonly ILogger<AdminCreditDriverTopUpCommandHandler>? _logger;

    public AdminCreditDriverTopUpCommandHandler(
        IDriverWalletRepository repository,
        ILogger<AdminCreditDriverTopUpCommandHandler>? logger = null)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<Result<DriverTopUpDto>> Handle(AdminCreditDriverTopUpCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return Result.Fail<DriverTopUpDto>("A reason is required for a manual top-up credit.");

        if (request.Amount is <= 0)
            return Result.Fail<DriverTopUpDto>("Credit amount must be greater than zero.");

        var topUp = await _repository.GetTopUpByIdAsync(request.TopUpId, ct);
        if (topUp == null)
            return Result.NotFound<DriverTopUpDto>("Top-up not found");

        // Already credited: return success rather than an error. The operator's intent - "this
        // driver should have their money" - is satisfied, and a retry must not look like a fault.
        if (topUp.CreditedAt.HasValue)
        {
            _logger?.LogInformation(
                "[TOPUP] [ADMIN] Top-up {TopUpId} was already credited at {CreditedAt}; no action taken.",
                topUp.Id, topUp.CreditedAt);
            return Result.Ok(DriverWalletMapper.ToTopUpDto(topUp));
        }

        var wallet = await _repository.GetWalletByDriverIdAsync(topUp.DriverId, ct);
        if (wallet == null)
        {
            // The wallet-not-found case is one of the reasons a top-up lands here, so create it
            // rather than making the operator go somewhere else first.
            wallet = await _repository.CreateWalletAsync(new DriverWallet(topUp.DriverId), ct);
        }

        var amount = request.Amount ?? topUp.Amount;

        topUp.MarkAsPaid(topUp.PaidAt ?? DateTime.UtcNow);
        wallet.AddTopUp(amount);

        var description = $"Top-up credited manually ({request.PerformedBy}): {request.Reason.Trim()}";

        // Carries the checkout id like the automatic path, so the unique index treats a manual
        // credit and an automatic one for the same payment as the same fact. The CreditedAt guard
        // above already covers the ordinary race; this is the second line, at the database.
        var transaction = new WalletTransaction(
            wallet.Id,
            WalletTransactionType.TopUp,
            WalletBucket.TopUp,
            amount,
            WalletTransactionStatus.Completed,
            description,
            providerPaymentId: topUp.ProviderPaymentId);

        topUp.MarkCredited();
        await _repository.SaveTopUpCreditAsync(topUp, wallet, transaction, ct);

        _logger?.LogWarning(
            "[TOPUP] [ADMIN] {PerformedBy} manually credited {Amount} to driver {DriverId} for top-up {TopUpId}. Reason: {Reason}",
            request.PerformedBy, amount, topUp.DriverId, topUp.Id, request.Reason);

        return Result.Ok(DriverWalletMapper.ToTopUpDto(topUp));
    }
}

/// <param name="SignedAmount">Positive credits the driver, negative debits them.</param>
/// <param name="Reason">Required, and stored on the ledger row for audit.</param>
/// <param name="PerformedBy">Recorded on the ledger row, so the adjustment names whoever made it.</param>
public record CreateCashDeficitAdjustmentCommand(
    Guid DriverId, decimal SignedAmount, string Reason, string PerformedBy) : IRequest<Result>;

/// <summary>
/// Manual admin correction for a cash-collection dispute.
///
/// Before this, a driver disputing an automated CashSettlementDebit - or a genuine under/over
/// remittance - had no correction path at all: WalletTransactionType.CashDeficitAdjustment existed
/// but nothing ever created one. Unlike the automated debit, this does not enforce the negative-
/// limit floor (see DriverWallet.AdjustTopUpForCashDeficit) since an admin may need to write off
/// debt past it, and it carries no amount-based idempotency key - it is a deliberate, audited
/// one-off action, not a redelivery-prone automated path.
/// </summary>
public class CreateCashDeficitAdjustmentCommandHandler : IRequestHandler<CreateCashDeficitAdjustmentCommand, Result>
{
    private readonly IDriverWalletRepository _repository;
    private readonly IDriverOutboxPublisher? _outboxPublisher;
    private readonly ILogger<CreateCashDeficitAdjustmentCommandHandler>? _logger;

    public CreateCashDeficitAdjustmentCommandHandler(
        IDriverWalletRepository repository,
        IDriverOutboxPublisher? outboxPublisher = null,
        ILogger<CreateCashDeficitAdjustmentCommandHandler>? logger = null)
    {
        _repository = repository;
        _outboxPublisher = outboxPublisher;
        _logger = logger;
    }

    public async Task<Result> Handle(CreateCashDeficitAdjustmentCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return Result.Fail("A reason is required for a manual wallet adjustment.");

        if (request.SignedAmount == 0)
            return Result.Fail("Adjustment amount must not be zero.");

        var wallet = await _repository.GetWalletByDriverIdAsync(request.DriverId, ct);
        if (wallet == null)
            wallet = await _repository.CreateWalletAsync(new DriverWallet(request.DriverId), ct);

        wallet.AdjustTopUpForCashDeficit(request.SignedAmount);

        var reason = request.Reason.Trim();
        var direction = request.SignedAmount > 0 ? "credit" : "debit";
        var description = $"Manual cash deficit {direction} of {Math.Abs(request.SignedAmount):0.00} by {request.PerformedBy}: {reason}";

        var transaction = new WalletTransaction(
            wallet.Id,
            WalletTransactionType.CashDeficitAdjustment,
            WalletBucket.TopUp,
            Math.Abs(request.SignedAmount),
            WalletTransactionStatus.Completed,
            description);

        // Staged before the SaveChanges that commits it, same reasoning as every other wallet-
        // balance change - see ApplyCashSettlementDebitCommandHandler.
        var adjustmentId = Guid.NewGuid();
        _outboxPublisher?.Publish(new CashDeficitAdjustedEvent(
            adjustmentId, request.DriverId, wallet.Id, request.SignedAmount, reason, request.PerformedBy, DateTime.UtcNow));

        await _repository.ApplyTransactionAsync(wallet, transaction, ct);

        _logger?.LogWarning(
            "[WALLET] [ADMIN] {PerformedBy} manually adjusted driver {DriverId}'s TopUp wallet by {SignedAmount}. Reason: {Reason}",
            request.PerformedBy, request.DriverId, request.SignedAmount, reason);

        return Result.Ok();
    }
}

/// <summary>
/// Records a declined top-up payment so the driver can see it (GitLab #64).
///
/// A failed attempt used to leave no trace anywhere: no ledger row, no push, no status change.
/// The record sat Pending for 25 hours and then quietly expired, so "my payment failed" and
/// "still waiting" looked identical in the app.
/// </summary>
/// <remarks>
/// Writes a Failed <see cref="WalletTransaction"/>, which is safe for balances: every aggregate
/// filters on Completed (or excludes Failed), and top-ups sit in the TopUp bucket that the
/// personal-balance calculation does not read. It moves no money.
///
/// Only genuine attempts reach here. An abandoned checkout is not a failed payment and must not
/// produce a row, or the list fills with noise from every driver who opened a link and wandered
/// off - which is the common case.
/// </remarks>
public class RecordDriverTopUpPaymentFailureCommandHandler : IRequestHandler<RecordDriverTopUpPaymentFailureCommand, Result>
{
    private readonly IDriverWalletRepository _repository;
    private readonly IDriverTopUpEventBroadcaster? _eventBroadcaster;
    private readonly ILogger<RecordDriverTopUpPaymentFailureCommandHandler>? _logger;

    public RecordDriverTopUpPaymentFailureCommandHandler(
        IDriverWalletRepository repository,
        IDriverTopUpEventBroadcaster? eventBroadcaster = null,
        ILogger<RecordDriverTopUpPaymentFailureCommandHandler>? logger = null)
    {
        _repository = repository;
        _eventBroadcaster = eventBroadcaster;
        _logger = logger;
    }

    public async Task<Result> Handle(RecordDriverTopUpPaymentFailureCommand request, CancellationToken ct)
    {
        // Joined by our own reference number first: the provider's failed-payment resource is a
        // payment, not a checkout, so its id does not match anything we stored.
        DriverTopUp? topUp = null;
        if (!string.IsNullOrWhiteSpace(request.ExternalId))
            topUp = await _repository.GetTopUpByExternalIdAsync(request.ExternalId, ct);

        topUp ??= await _repository.GetTopUpByProviderPaymentIdAsync(request.Provider, request.ProviderPaymentId, ct);

        if (topUp == null)
            // Ordinary: booking payments fail too, and they are not top-ups.
            return Result.NotFound("Top-up not found");

        // A decline for an earlier attempt can arrive after a later attempt succeeded. Recording
        // it then would contradict a credited wallet.
        if (topUp.IsSettled)
        {
            _logger?.LogInformation(
                "[TOPUP] Ignoring declined payment {ProviderPaymentId} for top-up {TopUpId}: already settled.",
                request.ProviderPaymentId, topUp.Id);
            return Result.Ok();
        }

        var wallet = await _repository.GetWalletByDriverIdAsync(topUp.DriverId, ct);
        if (wallet == null)
            return Result.Fail("Wallet not found");

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Payment declined" : request.Reason.Trim();

        // Keyed on the provider's payment id - now a real column with a unique index behind it
        // (GitLab #66), not the description text - so a redelivered webhook cannot stack
        // duplicate rows. Each attempt has its own id, so two genuine declines still produce two.
        var description = $"Top-up payment declined ({request.Provider} {request.ProviderPaymentId}): {reason}";
        if (await _repository.HasTransactionForProviderPaymentAsync(
                wallet.Id, request.ProviderPaymentId, WalletTransactionType.TopUp, ct))
            return Result.Ok();

        topUp.RecordFailedAttempt(reason);

        var transaction = new WalletTransaction(
            wallet.Id,
            WalletTransactionType.TopUp,
            WalletBucket.TopUp,
            topUp.Amount,
            WalletTransactionStatus.Failed,
            description,
            providerPaymentId: request.ProviderPaymentId);

        // The top-up carries a new FailureReason and the row is new, so both are saved together.
        await _repository.SaveTopUpCreditAsync(topUp, wallet, transaction, ct);

        BeeMetrics.DriverTopUpPaymentsFailed.Add(1, new KeyValuePair<string, object?>("provider", request.Provider));
        _logger?.LogInformation(
            "[TOPUP] Recorded declined payment for top-up {TopUpId} (driver {DriverId}, {Amount}): {Reason}",
            topUp.Id, topUp.DriverId, topUp.Amount, reason);

        if (_eventBroadcaster != null)
        {
            try
            {
                await _eventBroadcaster.PublishFailedAsync(new DriverTopUpFailedPayload(
                    topUp.Id,
                    topUp.DriverId,
                    topUp.Amount,
                    reason,
                    // The record is still open, so the existing checkout link is worth offering.
                    // A terminal session arrives separately as an expiry event.
                    CanRetry: topUp.Status == DriverTopUpStatus.Pending,
                    topUp.ProviderCheckoutUrl,
                    request.FailedAt ?? DateTime.UtcNow), ct);
            }
            catch
            {
                // Real-time push is best-effort; the ledger row is the durable record.
            }
        }

        return Result.Ok();
    }
}

public class ReleaseWithdrawalReservationCommandHandler : IRequestHandler<ReleaseWithdrawalReservationCommand, Result>
{
    private readonly IDriverWalletRepository _repository;
    private readonly IDriverOutboxPublisher _outboxPublisher;
    private readonly ILogger<ReleaseWithdrawalReservationCommandHandler> _logger;

    public ReleaseWithdrawalReservationCommandHandler(
        IDriverWalletRepository repository,
        IDriverOutboxPublisher outboxPublisher,
        ILogger<ReleaseWithdrawalReservationCommandHandler> logger)
    {
        _repository = repository;
        _outboxPublisher = outboxPublisher;
        _logger = logger;
    }

    public async Task<Result> Handle(ReleaseWithdrawalReservationCommand request, CancellationToken ct)
    {
        var withdrawal = await _repository.GetWithdrawalRequestByIdAsync(request.WithdrawalId, ct);
        if (withdrawal == null)
            return Result.Fail("Withdrawal not found");

        // Re-read the status rather than trusting the caller's snapshot: a webhook may have
        // resolved this between the reconciliation query and now.
        if (withdrawal.Status != WithdrawalStatus.Pending)
        {
            _logger.LogInformation(
                "[WITHDRAWAL] [RELEASE] Withdrawal {WithdrawalId} is {Status}, no longer a reservation — skipping",
                withdrawal.Id, withdrawal.Status);
            return Result.Ok();
        }

        var wallet = withdrawal.Wallet;
        if (wallet == null) return Result.Fail("Wallet not found");

        var transaction = await _repository.GetWithdrawalTransactionByRequestIdAsync(withdrawal.Id, ct);
        if (transaction == null) return Result.Fail("Withdrawal transaction not found");

        wallet.RejectWithdrawal(withdrawal.Amount);
        withdrawal.MarkAsFailed(request.Reason);
        transaction.MarkAsFailed();

        // Deliberately NO WithdrawalFailedEvent. That event is a ledger *reversal* (credit
        // DriverPersonalWallet, debit PendingPayout) of WithdrawalRequestedEvent — and a
        // reservation that never reached the provider never published Requested, so there is
        // nothing to reverse. Emitting it would post a phantom credit to the driver's wallet
        // account. The wallet balance itself is corrected above, in the database.
        await _repository.SaveWithdrawalDisbursementResultAsync(withdrawal, wallet, transaction, ct);

        _logger.LogWarning("[WITHDRAWAL] [RELEASE] Reservation {WithdrawalId} released: {Reason}", withdrawal.Id, request.Reason);
        return Result.Ok();
    }
}

public class AdoptWithdrawalDisbursementCommandHandler : IRequestHandler<AdoptWithdrawalDisbursementCommand, Result>
{
    private readonly IDriverWalletRepository _repository;
    private readonly IDriverOutboxPublisher _outboxPublisher;
    private readonly ILogger<AdoptWithdrawalDisbursementCommandHandler> _logger;

    public AdoptWithdrawalDisbursementCommandHandler(
        IDriverWalletRepository repository,
        IDriverOutboxPublisher outboxPublisher,
        ILogger<AdoptWithdrawalDisbursementCommandHandler> logger)
    {
        _repository = repository;
        _outboxPublisher = outboxPublisher;
        _logger = logger;
    }

    public async Task<Result> Handle(AdoptWithdrawalDisbursementCommand request, CancellationToken ct)
    {
        var withdrawal = await _repository.GetWithdrawalRequestByIdAsync(request.WithdrawalId, ct);
        if (withdrawal == null)
            return Result.Fail("Withdrawal not found");

        if (withdrawal.Status != WithdrawalStatus.Pending)
            return Result.Ok();

        // Never adopt a transfer whose amount disagrees with ours. A reference-number lookup
        // returning the wrong record would otherwise attach a stranger's payout to this
        // driver's withdrawal and settle it as if it were theirs.
        if (request.Amount != withdrawal.Amount)
        {
            _logger.LogCritical(
                "[WITHDRAWAL] [ADOPT] REFUSED - withdrawal {WithdrawalId} is {Expected} but provider transfer {DisbursementId} is {Actual}. Not adopting; needs manual review.",
                withdrawal.Id, withdrawal.Amount, request.ProviderDisbursementId, request.Amount);
            return Result.Fail("Provider transfer amount does not match the withdrawal");
        }

        // Promote to Approved so the normal reconciliation/webhook path owns it from here.
        withdrawal.SetProviderDisbursement(request.Provider, request.ProviderDisbursementId);
        withdrawal.AutoApprove();

        // The money DID leave, so accounting needs the forward entry it never got when the
        // create response was lost. Without it, the later Completed event would debit a
        // PendingPayout that was never credited.
        _outboxPublisher.Publish(new WithdrawalRequestedEvent(
            withdrawal.Id,
            withdrawal.DriverId,
            withdrawal.WalletId,
            withdrawal.Amount,
            "PHP",
            withdrawal.RequestedAt,
            withdrawal.IdempotencyKey,
            ProviderPayoutId: request.ProviderDisbursementId));

        await _repository.UpdateWithdrawalRequestAsync(withdrawal, ct);

        _logger.LogInformation(
            "[WITHDRAWAL] [ADOPT] Withdrawal {WithdrawalId} adopted provider transfer {DisbursementId} ({RawStatus})",
            withdrawal.Id, request.ProviderDisbursementId, request.RawStatus);
        return Result.Ok();
    }
}

public class ProcessDisbursementWebhookCommandHandler : IRequestHandler<ProcessDisbursementWebhookCommand, Result>
{
    private readonly IDriverWalletRepository _repository;
    private readonly IDriverOutboxPublisher _outboxPublisher;
    private readonly ILogger<ProcessDisbursementWebhookCommandHandler> _logger;

    public ProcessDisbursementWebhookCommandHandler(
        IDriverWalletRepository repository,
        IDriverOutboxPublisher outboxPublisher,
        ILogger<ProcessDisbursementWebhookCommandHandler> logger)
    {
        _repository = repository;
        _outboxPublisher = outboxPublisher;
        _logger = logger;
    }

    public async Task<Result> Handle(ProcessDisbursementWebhookCommand request, CancellationToken ct)
    {
        var withdrawal = await _repository.GetWithdrawalByProviderDisbursementIdAsync(request.Provider, request.DisbursementId, ct);
        if (withdrawal == null)
        {
            _logger.LogInformation("[DISBURSEMENT] [WEBHOOK] Unknown disbursement id {DisbursementId}, ignoring", request.DisbursementId);
            return Result.Ok();
        }

        if (withdrawal.Status == WithdrawalStatus.Completed || withdrawal.Status == WithdrawalStatus.Failed)
        {
            // A success arriving for a withdrawal we already wrote off means the money DID
            // leave and we have already returned it to the driver's balance — the same amount
            // is now both spent and spendable. Nothing can be auto-corrected safely here, so
            // it is escalated rather than swallowed by the idempotent skip below.
            if (withdrawal.Status == WithdrawalStatus.Failed &&
                request.Status.Equals("SUCCEEDED", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogCritical(
                    "[DISBURSEMENT] [WEBHOOK] [DOUBLE_SPEND] {Provider} reports transfer {DisbursementId} SUCCEEDED but withdrawal {WithdrawalId} (driver {DriverId}, {Amount}) was already marked Failed and refunded. The funds have left AND been returned. Manual correction required.",
                    request.Provider, request.DisbursementId, withdrawal.Id, withdrawal.DriverId, withdrawal.Amount);
                return Result.Ok();
            }

            _logger.LogInformation("[DISBURSEMENT] [WEBHOOK] Withdrawal {WithdrawalId} already {Status}, idempotent skip", withdrawal.Id, withdrawal.Status);
            return Result.Ok();
        }

        var wallet = withdrawal.Wallet;
        if (wallet == null)
        {
            _logger.LogWarning("[DISBURSEMENT] [WEBHOOK] Withdrawal {WithdrawalId} has no wallet", withdrawal.Id);
            return Result.Fail("Wallet not found");
        }

        // --- PayMongo child wallet (issue #91) ---
        // A withdrawal from the driver's own PayMongo wallet reserved nothing locally: there is no
        // PendingPayout to release and no WalletTransaction to settle. Falling through would end
        // badly in three separate ways - the transaction lookup below returns null and the
        // withdrawal sticks in Approved forever; CompleteWithdrawal would throw on an empty
        // PendingPayout; and RejectWithdrawal would CREDIT the mirror on failure, inventing money
        // the driver never lost locally.
        if (wallet.UsesPayMongoWallet && withdrawal.WalletId == wallet.Id)
        {
            return await CompleteChildWalletWithdrawalAsync(withdrawal, wallet, request, ct);
        }

        var transaction = await _repository.GetWithdrawalTransactionByRequestIdAsync(withdrawal.Id, ct);
        if (transaction == null)
        {
            _logger.LogWarning("[DISBURSEMENT] [WEBHOOK] No withdrawal transaction for request {WithdrawalId}", withdrawal.Id);
            return Result.Fail("Withdrawal transaction not found");
        }

        var status = request.Status.ToUpperInvariant();
        var completedAt = DateTime.UtcNow;

        if (status == "SUCCEEDED")
        {
            withdrawal.MarkAsCompleted();
            wallet.CompleteWithdrawal(withdrawal.Amount);
            transaction.MarkAsCompleted();
            // Add outbox message BEFORE SaveChanges for atomicity
            _outboxPublisher.Publish(new WithdrawalCompletedEvent(
                withdrawal.Id,
                withdrawal.DriverId,
                withdrawal.Amount,
                "PHP",
                completedAt));
            await _repository.SaveWithdrawalDisbursementResultAsync(withdrawal, wallet, transaction, ct);
            _logger.LogInformation("[DISBURSEMENT] [WEBHOOK] Withdrawal {WithdrawalId} completed", withdrawal.Id);
            return Result.Ok();
        }

        if (status is "FAILED" or "REVERSED" or "CANCELLED")
        {
            var reason = string.IsNullOrWhiteSpace(request.FailureReason)
                ? $"Payout {request.Event} ({request.Status})"
                : request.FailureReason;
            withdrawal.MarkAsFailed(reason);
            wallet.RejectWithdrawal(withdrawal.Amount);
            transaction.MarkAsFailed();
            // Add outbox message BEFORE SaveChanges for atomicity
            _outboxPublisher.Publish(new WithdrawalFailedEvent(
                withdrawal.Id,
                withdrawal.DriverId,
                withdrawal.Amount,
                "PHP",
                reason,
                completedAt));
            await _repository.SaveWithdrawalDisbursementResultAsync(withdrawal, wallet, transaction, ct);
            _logger.LogInformation("[DISBURSEMENT] [WEBHOOK] Withdrawal {WithdrawalId} failed: {Reason}", withdrawal.Id, reason);
            return Result.Ok();
        }

        _logger.LogInformation("[DISBURSEMENT] [WEBHOOK] Withdrawal {WithdrawalId} status {Status} not applied (no-op)", withdrawal.Id, request.Status);
        return Result.Ok();
    }

    /// <summary>
    /// Settles a withdrawal that left the driver's own PayMongo wallet.
    /// <para>
    /// The mirror follows the money rather than being reserved against it: on success the balance
    /// comes down because the funds have genuinely left PayMongo, and on failure it is left alone
    /// because they never did. There is no PendingPayout on this path, so none of the reservation
    /// methods apply.
    /// </para>
    /// </summary>
    private async Task<Result> CompleteChildWalletWithdrawalAsync(
        WithdrawalRequest withdrawal,
        DriverWallet wallet,
        ProcessDisbursementWebhookCommand request,
        CancellationToken ct)
    {
        var status = request.Status.ToUpperInvariant();
        var completedAt = DateTime.UtcNow;

        if (status == "SUCCEEDED")
        {
            withdrawal.MarkAsCompleted();
            // The money is gone from PayMongo, so the mirror must come down to match. Skipping this
            // would leave the driver seeing a balance they no longer have - and although the proxy
            // reads PayMongo's balance before every payout so they still cannot overdraw, the
            // displayed figure would simply be wrong.
            //
            // Amount PLUS fee: PayMongo charges the fee to the source wallet, so the driver's
            // wallet loses both. The platform path below uses Amount alone, correctly - there the
            // fee is ours and the driver reserved exactly the amount.
            wallet.SubtractForRefund(withdrawal.TotalDebitedFromSource);

            _outboxPublisher.Publish(new WithdrawalCompletedEvent(
                withdrawal.Id, withdrawal.DriverId, withdrawal.Amount, "PHP", completedAt));

            await _repository.UpdateWithdrawalRequestAsync(withdrawal, ct);
            await _repository.UpdateWalletAsync(wallet, ct);

            _logger.LogInformation(
                "[DISBURSEMENT] [WEBHOOK] [CHILD_WALLET] Withdrawal {WithdrawalId} completed for driver {DriverId}",
                withdrawal.Id, withdrawal.DriverId);
            return Result.Ok();
        }

        if (status is "FAILED" or "REVERSED" or "CANCELLED")
        {
            var reason = string.IsNullOrWhiteSpace(request.FailureReason)
                ? $"Payout {request.Event} ({request.Status})"
                : request.FailureReason;

            withdrawal.MarkAsFailed(reason);
            // Deliberately no balance change: nothing was deducted locally when the request was
            // made, so there is nothing to give back. Crediting here would invent money.
            await _repository.UpdateWithdrawalRequestAsync(withdrawal, ct);

            _logger.LogInformation(
                "[DISBURSEMENT] [WEBHOOK] [CHILD_WALLET] Withdrawal {WithdrawalId} failed: {Reason}",
                withdrawal.Id, reason);
            return Result.Ok();
        }

        _logger.LogInformation(
            "[DISBURSEMENT] [WEBHOOK] [CHILD_WALLET] Ignoring non-terminal status {Status} for {WithdrawalId}",
            request.Status, withdrawal.Id);
        return Result.Ok();
    }
}

/// <summary>
/// Which drivers can be offered a cash job of a given fare — those we could actually collect the
/// commission from afterwards (issue #102).
/// </summary>
/// <remarks>
/// Offering a job to a driver who cannot pay the fee on it is how arrears are created. This is the
/// prevention; the sweep in <see cref="ApplyCashSettlementDebitCommandHandler"/> is the cure.
/// <para>
/// Reads the local mirror, never PayMongo. This sits on the dispatch path in front of every booking,
/// where a provider call per candidate would be unaffordable — and the mirror is exact for money
/// leaving, because every sweep debits it in the same write that moves the money.
/// </para>
/// <para>
/// Also the cashbond gate (issue #103): sitting in front of every booking is exactly why an unpaid
/// cashbond is filtered here too, in <see cref="IDriverWalletRepository.GetDriversEligibleForCashJobAsync"/>,
/// rather than adding a second cross-module check.
/// </para>
/// </remarks>
public class GetDriversEligibleForCashJobQueryHandler
    : IRequestHandler<GetDriversEligibleForCashJobQuery, IReadOnlyList<Guid>>
{
    private readonly IDriverWalletRepository _repository;
    private readonly DriverWalletOptions _options;

    public GetDriversEligibleForCashJobQueryHandler(
        IDriverWalletRepository repository,
        IOptions<DriverWalletOptions> options)
    {
        _repository = repository;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<Guid>> Handle(
        GetDriversEligibleForCashJobQuery request, CancellationToken ct)
    {
        // Rounded the same way the charge itself is (CashDeliverySettlementConsumer), so a driver is
        // never offered a job whose fee is a centavo more than the balance we checked.
        var commission = decimal.Round(
            request.Fare * _options.CashDeliveryPlatformChargeRate, 2, MidpointRounding.AwayFromZero);

        return await _repository.GetDriversEligibleForCashJobAsync(request.DriverIds, commission, ct);
    }
}

/// <summary>
/// Collects the platform's share of a cash delivery by sweeping it from the driver's own PayMongo
/// wallet into the platform's (issue #102).
/// </summary>
/// <remarks>
/// On a cash job the customer pays the driver directly, so the driver walks away holding our
/// commission. This used to be recorded as a debit against the Cash Wallet float — a bucket that is
/// not an active feature and that nobody funds, so the fee was booked and never collected. The money
/// is in the driver's child wallet, so that is where it is taken from.
/// </remarks>
public class ApplyCashSettlementDebitCommandHandler : IRequestHandler<ApplyCashSettlementDebitCommand, Result>
{
    private readonly IDriverWalletRepository _repository;
    private readonly DriverWalletOptions _options;
    private readonly IDriverOutboxPublisher? _outboxPublisher;
    private readonly IPayMongoAccountsClient? _accounts;
    private readonly ILogger<ApplyCashSettlementDebitCommandHandler>? _logger;

    public ApplyCashSettlementDebitCommandHandler(
        IDriverWalletRepository repository,
        IOptions<DriverWalletOptions> options,
        IDriverOutboxPublisher? outboxPublisher = null,
        IPayMongoAccountsClient? accounts = null,
        ILogger<ApplyCashSettlementDebitCommandHandler>? logger = null)
    {
        _repository = repository;
        _options = options.Value;
        _outboxPublisher = outboxPublisher;
        _accounts = accounts;
        _logger = logger;
    }

    public async Task<Result> Handle(ApplyCashSettlementDebitCommand request, CancellationToken ct)
    {
        var wallet = await _repository.GetWalletByDriverIdAsync(request.DriverId, ct);
        if (wallet == null)
            wallet = await _repository.CreateWalletAsync(new DriverWallet(request.DriverId), ct);

        // Keyed on the booking id, not on a formatted description. The description-matching
        // guard broke the moment the stored text differed from the text looked up (see the
        // earning path's history, WalletTransaction.cs), so this uses the same booking-scoped
        // check as EarningCreditConsumer, backed by the unique index on
        // (WalletId, RelatedBookingId, Type).
        var existing = await _repository.GetTransactionForBookingAsync(
            wallet.Id, request.BookingId, WalletTransactionType.PlatformCommission, ct);

        if (existing is { Status: WalletTransactionStatus.Completed })
            return Result.Ok();

        try
        {
            // Anything the driver still owes from an earlier job is collected first. A retry here
            // is the only routine way arrears clear: the row stays Pending until a later job finds
            // the balance to cover it.
            await SettleArrearsAsync(wallet, request.BookingId, ct);

            // Re-read: the arrears sweeps above just spent part of the balance.
            var claim = existing ?? await ClaimAsync(wallet, request, ct);

            await SweepAsync(wallet, claim, request.DriverId, request.BookingId, ct);

            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Writes the commission row <b>before</b> any money moves.
    /// </summary>
    /// <remarks>
    /// The issue #90 ordering, as used by <c>EarningCreditConsumer</c>: the unique index on
    /// (WalletId, RelatedBookingId, Type) is what stops a redelivered BookingCompletedEvent
    /// charging twice, and it can only do that if the row exists before the transfer is attempted.
    /// Written Pending, which is also how an uncollectable fee is recorded — the row is the debt.
    /// </remarks>
    private async Task<WalletTransaction> ClaimAsync(
        DriverWallet wallet, ApplyCashSettlementDebitCommand request, CancellationToken ct)
    {
        var claim = new WalletTransaction(
            wallet.Id,
            WalletTransactionType.PlatformCommission,
            // Personal, not TopUp: this comes out of the driver's own PayMongo wallet. The Cash
            // Wallet float is not an active feature and is never touched here.
            WalletBucket.Personal,
            request.Amount,
            WalletTransactionStatus.Pending,
            $"Platform commission for booking {request.BookingId}",
            request.BookingId);

        await _repository.CreateTransactionAsync(claim, ct);
        return claim;
    }

    /// <summary>
    /// Moves the commission from the driver's PayMongo wallet to the platform's, and commits the
    /// row and the mirror together.
    /// </summary>
    /// <remarks>
    /// All-or-nothing. A partial sweep would leave a row that is neither Pending nor Completed in
    /// any way the status can express, and the offer gate means a driver should not have been given
    /// a job they could not cover in the first place — a short balance here is a withdrawal that
    /// landed mid-job, not the normal path. The row stays Pending and the next job retries it.
    /// </remarks>
    private async Task SweepAsync(
        DriverWallet wallet, WalletTransaction claim, Guid driverId, Guid bookingId, CancellationToken ct)
    {
        if (!wallet.UsesPayMongoWallet || _accounts is null)
        {
            // Unreachable in practice: a driver with no child wallet is filtered out of dispatch,
            // so they are never offered the job that gets here. Logged rather than thrown because
            // the delivery has already happened - refusing to record the fee would lose it.
            _logger?.LogWarning(
                "[PAYMONGO] [COMMISSION] Driver {DriverId} completed cash booking {BookingId} with no "
                + "BeeWallet to sweep from; {Amount} recorded as owed.",
                driverId, bookingId, claim.Amount);
            return;
        }

        if (wallet.Balance < claim.Amount)
        {
            _logger?.LogWarning(
                "[PAYMONGO] [COMMISSION] Driver {DriverId} holds {Balance} against a {Amount} commission "
                + "for booking {BookingId}; left owing and retried on their next job.",
                driverId, wallet.Balance, claim.Amount, bookingId);
            return;
        }

        PayMongoInternalTransfer transfer;
        try
        {
            transfer = await _accounts.SweepFromChildAsync(
                wallet.PayMongoAccountId!,
                wallet.PayMongoAccountNumber!,
                wallet.AccountHolderName ?? "Driver",
                claim.Amount,
                $"comm-{bookingId:N}",
                $"Platform commission for booking {bookingId}",
                ct);
        }
        catch (Exception ex)
        {
            // Includes PayoutsNotConfiguredException. The row stays Pending, so this is retried
            // rather than lost - and rethrowing puts the message back through MassTransit's
            // redelivery, where the booking-id check makes a second attempt safe.
            _logger?.LogError(ex,
                "[PAYMONGO] [COMMISSION] Sweep failed for driver {DriverId}, booking {BookingId}",
                driverId, bookingId);
            throw;
        }

        if (transfer.Failed)
        {
            // Marked failed rather than deleted, so a retry sees a definite outcome instead of an
            // ambiguous gap - the money never left, so the mirror must not move either.
            claim.MarkAsFailed();
            await _repository.UpdateTransactionAsync(claim, ct);

            _logger?.LogError(
                "[PAYMONGO] [COMMISSION] PayMongo rejected the sweep for driver {DriverId}, booking "
                + "{BookingId}: {Reason}",
                driverId, bookingId, transfer.ProviderErrorMessage ?? "no reason given");

            throw new InvalidOperationException(
                $"PayMongo rejected the commission sweep for booking {bookingId}.");
        }

        wallet.DebitForPlatformCommission(claim.Amount);
        claim.MarkAsCompleted();
        claim.SetProviderPaymentId(transfer.TransferId);

        // Staged before the SaveChanges that commits it, so the outbox row and the wallet debit
        // land in the same transaction (see IDriverOutboxPublisher).
        _outboxPublisher?.Publish(new CashSettlementDebitedEvent(
            bookingId, driverId, wallet.Id, claim.Amount, DateTime.UtcNow));

        // Row and balance in one SaveChanges: committing them separately could leave a completed
        // row with no balance change, which the guard above would then read as work already done.
        await _repository.SaveClaimedTransactionAsync(wallet, claim, ct);

        _logger?.LogInformation(
            "[PAYMONGO] [COMMISSION] Swept {Amount} from driver {DriverId} for booking {BookingId} "
            + "({TransferId})",
            claim.Amount, driverId, bookingId, transfer.TransferId);
    }

    /// <summary>Re-sweeps commission left owing by earlier jobs, oldest first.</summary>
    private async Task SettleArrearsAsync(DriverWallet wallet, Guid currentBookingId, CancellationToken ct)
    {
        var unpaid = await _repository.GetUnpaidCommissionsAsync(wallet.Id, ct);

        foreach (var arrear in unpaid)
        {
            // The current booking's own row is handled by the caller; sweeping it here would charge
            // it twice over in the same pass.
            if (arrear.RelatedBookingId == currentBookingId) continue;
            if (wallet.Balance < arrear.Amount) break;   // ordered oldest first, so stop at the first gap

            await SweepAsync(wallet, arrear, wallet.DriverId, arrear.RelatedBookingId ?? currentBookingId, ct);
        }
    }
}

public class GetDriverWalletRiskQueryHandler : IRequestHandler<GetDriverWalletRiskQuery, Result<IReadOnlyList<DriverWalletDto>>>
{
    private readonly IDriverWalletRepository _repository;
    private readonly DriverWalletOptions _options;

    public GetDriverWalletRiskQueryHandler(IDriverWalletRepository repository, IOptions<DriverWalletOptions> options)
    {
        _repository = repository;
        _options = options.Value;
    }

    public async Task<Result<IReadOnlyList<DriverWalletDto>>> Handle(GetDriverWalletRiskQuery request, CancellationToken ct)
    {
        var threshold = _options.DefaultCashJobBlockThreshold;
        var wallets = await _repository.GetWalletsBelowTopUpThresholdAsync(threshold, ct);

        // One query for the whole list rather than one per wallet.
        var owing = await _repository.GetWalletIdsWithUnpaidCommissionAsync(
            wallets.Select(w => w.Id).ToList(), ct);

        var dtos = wallets.Select(w => new DriverWalletDto(
            w.Id,
            w.DriverId,
            w.Balance,
            w.TopUpBalance,
            w.PendingPayout,
            w.IsEligibleForCashJobs(owing.Contains(w.Id)),
            w.BankAccountNumber,
            w.BankName,
            w.AccountHolderName,
            w.LastUpdatedAt)).ToList();
        return Result.Ok<IReadOnlyList<DriverWalletDto>>(dtos);
    }
}

public class GetAllDriverTopUpsQueryHandler : IRequestHandler<GetAllDriverTopUpsQuery, Result<IReadOnlyList<DriverTopUpDto>>>
{
    private readonly IDriverWalletRepository _repository;

    public GetAllDriverTopUpsQueryHandler(IDriverWalletRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<DriverTopUpDto>>> Handle(GetAllDriverTopUpsQuery request, CancellationToken ct)
    {
        var topUps = await _repository.GetTopUpsAsync(request.From, request.To, request.Status, ct);
        return Result.Ok<IReadOnlyList<DriverTopUpDto>>(topUps.Select(DriverWalletMapper.ToTopUpDto).ToList());
    }
}

internal static class DriverWalletMapper
{
    public static DriverTopUpDto ToTopUpDto(DriverTopUp topUp)
        => new(
            topUp.Id,
            topUp.DriverId,
            topUp.WalletId,
            topUp.Amount,
            topUp.Status,
            topUp.ExternalId,
            topUp.IdempotencyKey,
            topUp.ProviderPaymentId,
            topUp.ProviderCheckoutUrl,
            topUp.ExpiresAt,
            topUp.PaidAt,
            topUp.CreditedAt,
            topUp.CreatedAt);
}
