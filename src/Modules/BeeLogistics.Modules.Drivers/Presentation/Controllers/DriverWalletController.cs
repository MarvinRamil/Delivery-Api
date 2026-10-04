using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BeeLogistics.Modules.Drivers.Application.DTOs;
using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeeLogistics.Modules.Drivers.Presentation.Controllers;

[Authorize]
[ApiController]
[Route("api/drivers")]
public class DriverWalletController : BaseController
{
    private readonly IMediator _mediator;
    private readonly IDriverTopUpEventBroadcaster? _topUpEventBroadcaster;

    public DriverWalletController(IMediator mediator, IDriverTopUpEventBroadcaster? topUpEventBroadcaster = null)
    {
        _mediator = mediator;
        _topUpEventBroadcaster = topUpEventBroadcaster;
    }

    private bool IsBackofficeAdmin()
    {
        return User.HasClaim(c => c.Type == "is_backoffice" && c.Value == "true") &&
               (User.IsInRole("SuperAdmin") || User.IsInRole("Admin"));
    }

    private bool IsSelfDriver(Guid driverId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(userId, out var currentUserId) && currentUserId == driverId;
    }

    private IActionResult? EnsureDriverAccess(Guid driverId)
    {
        if (IsBackofficeAdmin() || IsSelfDriver(driverId))
            return null;

        return Forbid();
    }

    [HttpGet("{driverId}/earnings")]
    public async Task<IActionResult> GetEarnings(
        Guid driverId,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var query = new GetDriverEarningsQuery(driverId, startDate, endDate);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    /// <summary>
    /// Earnings history with detailed 5% platform fee breakdown (fare, platform fee, net) for driver verification.
    /// </summary>
    [HttpGet("{driverId}/earnings/history")]
    public async Task<IActionResult> GetEarningsHistory(
        Guid driverId,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] int? limit = null,
        CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var query = new GetDriverEarningsHistoryQuery(driverId, startDate, endDate, limit);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    [HttpGet("{driverId}/wallet")]
    public async Task<IActionResult> GetWallet(Guid driverId, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var query = new GetDriverWalletQuery(driverId);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    [HttpGet("{driverId}/wallet/eligibility/cash-jobs")]
    public async Task<IActionResult> GetCashJobEligibility(Guid driverId, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var query = new GetCashJobEligibilityQuery(driverId);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    [HttpGet("{driverId}/wallet/transactions")]
    public async Task<IActionResult> GetTransactions(
        Guid driverId,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] WalletTransactionType? type = null,
        CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var query = new GetWalletTransactionsQuery(driverId, startDate, endDate, type);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    /// <summary>
    /// Get withdrawal requests (history) for the driver. Account number is masked in response.
    /// </summary>
    [HttpGet("{driverId}/withdrawals")]
    public async Task<IActionResult> GetWithdrawals(
        Guid driverId,
        [FromQuery] WithdrawalStatus? status = null,
        CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var query = new GetDriverWithdrawalRequestsQuery(driverId, status);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    // ── PayMongo child-account setup (issue #91) ───────────────────────
    // Behind DriverWallet:PayMongoOnboardingEnabled. No money moves through these; they open the
    // driver's own PayMongo wallet so earnings can later be held there instead of by us.

    /// <summary>
    /// Opens the driver's PayMongo wallet and returns the hosted identity-verification link.
    /// Safe to call again: it re-issues a verification session rather than opening a second account.
    /// </summary>
    [HttpPost("{driverId}/wallet/paymongo/start")]
    public async Task<IActionResult> StartPayMongoOnboarding(Guid driverId, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var result = await _mediator.Send(new StartPayMongoOnboardingCommand(driverId), ct);
        return FromResult(result);
    }

    /// <summary>
    /// Submits the remaining details and activates the wallet, once verification has passed.
    /// </summary>
    /// <remarks>
    /// Activation is irreversible and freezes the account: PayMongo rejects every later edit, and a
    /// decline cannot be appealed or retried on the same account. Details must be right first time.
    /// </remarks>
    [HttpPost("{driverId}/wallet/paymongo/activate")]
    public async Task<IActionResult> ActivatePayMongoWallet(
        Guid driverId,
        [FromBody] SubmitPayMongoOnboardingDetailsDto dto,
        CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var result = await _mediator.Send(new SubmitPayMongoOnboardingDetailsCommand(
            driverId,
            dto.MiddleName,
            dto.Nationality,
            dto.NatureOfWork,
            dto.SourceOfFunds,
            dto.Tin,
            dto.PlaceOfBirthCity,
            dto.AddressLine1,
            dto.AddressCity,
            dto.AddressState,
            dto.AddressPostalCode,
            dto.SourceOfFundsOther,
            dto.MobileNumber), ct);

        return FromResult(result);
    }

    [HttpGet("{driverId}/wallet/paymongo")]
    public async Task<IActionResult> GetPayMongoOnboardingStatus(Guid driverId, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var result = await _mediator.Send(new GetPayMongoOnboardingStatusQuery(driverId), ct);
        return FromResult(result);
    }

    /// <summary>
    /// The most this driver can withdraw right now, and the fee that explains the gap.
    /// </summary>
    /// <summary>
    /// A QR the driver scans to add money to their own BeeWallet wallet, credited in real time.
    /// </summary>
    /// <param name="amount">
    /// Optional. Fixes the amount in the code so the payer cannot mistype it, at the cost of an
    /// expiry — an amount makes the QR dynamic. Omitted, the driver gets the reusable code they
    /// can show anyone.
    /// </param>
    // Both paths serve the same action. The app and the backend deploy independently, so removing
    // the old one would break the QR for every already-installed app until it updated. The beepay
    // path is kept for that reason alone, and can go once no released app calls it.
    [HttpGet("{driverId}/wallet/beewallet/topup-qr")]
    [HttpGet("{driverId}/wallet/beepay/topup-qr")]
    public async Task<IActionResult> GetBeeWalletTopUpQr(
        Guid driverId, [FromQuery] decimal? amount = null, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var result = await _mediator.Send(new GetBeeWalletTopUpQrQuery(driverId, amount), ct);
        return FromResult(result);
    }

    [HttpGet("{driverId}/wallet/withdrawable")]
    public async Task<IActionResult> GetWithdrawable(Guid driverId, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var result = await _mediator.Send(new GetWithdrawableBalanceQuery(driverId), ct);
        return FromResult(result);
    }

    [HttpPost("{driverId}/wallet/withdraw")]
    public async Task<IActionResult> RequestWithdrawal(
        Guid driverId,
        [FromBody] CreateWithdrawalRequestDto dto,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKeyHeader = null,
        CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var idempotencyKey = !string.IsNullOrWhiteSpace(idempotencyKeyHeader)
            ? idempotencyKeyHeader.Trim()
            : dto.IdempotencyKey;

        var command = new RequestWithdrawalCommand(
            driverId,
            dto.Amount,
            dto.SavedWithdrawalMethodId,
            dto.BankAccountNumber,
            dto.BankName,
            dto.AccountHolderName,
            dto.BankCode,
            idempotencyKey,
            dto.DestinationType,
            dto.QrString
        );
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    [HttpPost("{driverId}/wallet/topup/create")]
    public async Task<IActionResult> CreateTopUp(
        Guid driverId,
        [FromBody] CreateDriverTopUpRequestDto dto,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKeyHeader = null,
        CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var idempotencyKey = !string.IsNullOrWhiteSpace(idempotencyKeyHeader)
            ? idempotencyKeyHeader
            : dto.IdempotencyKey;

        var command = new CreateDriverTopUpCommand(driverId, dto.Amount, dto.PayerEmail, dto.Description, idempotencyKey);
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    [HttpGet("{driverId}/wallet/topup/{topUpId:guid}")]
    public async Task<IActionResult> GetTopUp(Guid driverId, Guid topUpId, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var query = new GetDriverTopUpQuery(driverId, topUpId);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    [HttpGet("{driverId}/wallet/topup/history")]
    public async Task<IActionResult> GetTopUpHistory(Guid driverId, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var query = new GetDriverTopUpHistoryQuery(driverId);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    /// <summary>
    /// SSE stream for real-time top-up paid events. When a webhook marks a top-up as paid, this stream receives an event so the app can update immediately.
    /// </summary>
    [HttpGet("{driverId}/wallet/topup/events")]
    [Produces("text/event-stream")]
    public async Task GetTopUpEvents(Guid driverId, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null)
        {
            await accessDenied.ExecuteResultAsync(new ControllerContext());
            return;
        }

        if (_topUpEventBroadcaster == null)
        {
            Response.StatusCode = 501;
            var notConfigured = Encoding.UTF8.GetBytes("Top-up events not configured");
            await Response.Body.WriteAsync(notConfigured, ct);
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        try
        {
            await foreach (var payload in _topUpEventBroadcaster.SubscribeAsync(driverId, ct))
            {
                var json = JsonSerializer.Serialize(new
                {
                    payload.TopUpId,
                    payload.DriverId,
                    payload.Amount,
                    payload.Status,
                    payload.PersonalBalance,
                    payload.TopUpBalance,
                    payload.PaidAtUtc
                });
                var chunk = Encoding.UTF8.GetBytes("data: " + json + "\n\n");
                await Response.Body.WriteAsync(chunk, ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
    }

    [HttpPost("{driverId}/wallet/topup/{topUpId:guid}/cancel")]
    public async Task<IActionResult> CancelTopUp(
        Guid driverId,
        Guid topUpId,
        [FromBody] CancelDriverTopUpRequestDto? dto = null,
        CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var reason = dto?.Reason;
        var command = new CancelDriverTopUpCommand(driverId, topUpId, reason);
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    [HttpPost("{driverId}/wallet/transfer")]
    public async Task<IActionResult> TransferWalletBalance(
        Guid driverId,
        [FromBody] TransferWalletBalanceRequestDto dto,
        CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var command = new TransferWalletBalanceCommand(driverId, dto.From, dto.To, dto.Amount);
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    // ── Cashbond (issue #103) ───────────────────────────────────────────
    // Fixed, vehicle-type-priced deposit paid once before a driver can be offered bookings.

    [HttpGet("{driverId}/wallet/cashbond")]
    public async Task<IActionResult> GetCashBondStatus(Guid driverId, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var result = await _mediator.Send(new GetDriverCashBondStatusQuery(driverId), ct);
        return FromResult(result);
    }

    /// <summary>
    /// The QR that pays this driver's cashbond into the platform wallet. Safe to call again — an
    /// outstanding payment re-issues a code for the same transaction rather than opening a second one.
    /// </summary>
    [HttpPost("{driverId}/wallet/cashbond/qr")]
    public async Task<IActionResult> CreateCashBondQr(Guid driverId, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var result = await _mediator.Send(new CreateCashBondQrCommand(driverId), ct);
        return FromResult(result);
    }

    [Authorize(Policy = "Backoffice")]
    [HttpGet("admin/wallets/{driverId:guid}/cashbond")]
    public async Task<IActionResult> GetCashBondStatusAdmin(Guid driverId, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetDriverCashBondStatusQuery(driverId), ct);
        return FromResult(result);
    }

    [Authorize(Policy = "Backoffice")]
    [HttpPost("admin/wallets/{driverId:guid}/cashbond/refund")]
    public async Task<IActionResult> RefundCashBond(Guid driverId, CancellationToken ct = default)
    {
        var performedBy = User.FindFirstValue("email")
                          ?? User.FindFirstValue(ClaimTypes.Email)
                          ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? "unknown-admin";

        var result = await _mediator.Send(new RefundCashBondCommand(driverId, performedBy), ct);
        return FromResult(result);
    }

    // ── Package insurance (issue #104) ──────────────────────────────────
    // Annual, vehicle-type-priced coverage for the packages a driver carries. Separate from the
    // driver's own vehicle insurance document (DriverApplication.InsurancePath).

    [HttpGet("{driverId}/wallet/package-insurance")]
    public async Task<IActionResult> GetPackageInsuranceStatus(Guid driverId, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var result = await _mediator.Send(new GetDriverPackageInsuranceStatusQuery(driverId), ct);
        return FromResult(result);
    }

    /// <summary>
    /// The QR that pays this driver's next-due package-insurance premium into the platform
    /// wallet. Safe to call again — an outstanding payment re-issues a code for the same
    /// transaction rather than opening a second one.
    /// </summary>
    [HttpPost("{driverId}/wallet/package-insurance/qr")]
    public async Task<IActionResult> CreateInsurancePremiumQr(Guid driverId, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var result = await _mediator.Send(new CreateInsurancePremiumQrCommand(driverId), ct);
        return FromResult(result);
    }

    [Authorize(Policy = "Backoffice")]
    [HttpGet("admin/wallets/{driverId:guid}/package-insurance")]
    public async Task<IActionResult> GetPackageInsuranceStatusAdmin(Guid driverId, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetDriverPackageInsuranceStatusQuery(driverId), ct);
        return FromResult(result);
    }

    [HttpGet("{driverId}/missions")]
    public async Task<IActionResult> GetMissions(
        Guid driverId,
        [FromQuery] MissionStatus? status = null,
        CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var query = new GetDriverMissionsQuery(driverId, status);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    [HttpPost("{driverId}/missions/{missionId}/claim")]
    public async Task<IActionResult> ClaimMissionReward(
        Guid driverId,
        Guid missionId,
        CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var command = new ClaimMissionRewardCommand(driverId, missionId);
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    [Authorize(Policy = "Backoffice")]
    [HttpGet("admin/wallets/risk")]
    public async Task<IActionResult> GetWalletRiskList(CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetDriverWalletRiskQuery(), ct);
        return FromResult(result);
    }

    [Authorize(Policy = "Backoffice")]
    [HttpGet("admin/topups")]
    public async Task<IActionResult> GetTopUps(
        [FromQuery] Guid? driverId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] DriverTopUpStatus? status = null,
        CancellationToken ct = default)
    {
        if (driverId.HasValue)
        {
            var result = await _mediator.Send(new GetDriverTopUpHistoryQuery(driverId.Value), ct);
            return FromResult(result);
        }

        var allResult = await _mediator.Send(new GetAllDriverTopUpsQuery(from, to, status), ct);
        return FromResult(allResult);
    }

    /// <summary>
    /// Credit a top-up the driver paid for that the automated flow could not settle.
    /// </summary>
    /// <remarks>
    /// The recovery path for GitLab #64. Before this, a top-up stuck by an amount mismatch, a
    /// missing wallet, or a premature expiry could only be fixed with hand-written SQL against
    /// DriverWallets and WalletTransactions.
    ///
    /// Idempotent: crediting an already-credited top-up succeeds without moving money again.
    /// </remarks>
    [Authorize(Policy = "Backoffice")]
    [HttpPost("admin/topups/{topUpId:guid}/credit")]
    public async Task<IActionResult> CreditTopUp(
        Guid topUpId,
        [FromBody] AdminCreditTopUpRequest request,
        CancellationToken ct = default)
    {
        // Recorded on the ledger row, so the adjustment names whoever made it.
        var performedBy = User.FindFirstValue("email")
                          ?? User.FindFirstValue(ClaimTypes.Email)
                          ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? "unknown-admin";

        var result = await _mediator.Send(
            new AdminCreditDriverTopUpCommand(topUpId, request.Amount, request.Reason, performedBy), ct);

        return FromResult(result);
    }

    /// <summary>
    /// Manually adjust a driver's TopUp wallet for a cash-collection dispute (a driver disputing
    /// an automated CashSettlementDebit, or a genuine under/over remittance). Before this endpoint
    /// existed, there was no correction path at all for a cash dispute.
    /// </summary>
    [Authorize(Policy = "Backoffice")]
    [HttpPost("admin/wallets/{driverId:guid}/adjustments")]
    public async Task<IActionResult> AdjustWallet(
        Guid driverId,
        [FromBody] AdjustWalletRequest request,
        CancellationToken ct = default)
    {
        var performedBy = User.FindFirstValue("email")
                          ?? User.FindFirstValue(ClaimTypes.Email)
                          ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? "unknown-admin";

        var result = await _mediator.Send(
            new CreateCashDeficitAdjustmentCommand(driverId, request.SignedAmount, request.Reason, performedBy), ct);

        return FromResult(result);
    }
}

/// <param name="Amount">Null credits the top-up's own amount; set it only to resolve a mismatch.</param>
/// <param name="Reason">Required, and stored on the ledger row for audit.</param>
public record AdminCreditTopUpRequest(string Reason, decimal? Amount = null);

/// <param name="SignedAmount">Positive credits the driver, negative debits them.</param>
/// <param name="Reason">Required, and stored on the ledger row for audit.</param>
public record AdjustWalletRequest(decimal SignedAmount, string Reason);

