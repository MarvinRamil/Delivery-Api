using BeeLogistics.Modules.Drivers.Application.DTOs;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Shared.Abstractions;
using MediatR;
using QRCoder;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Drivers.Application.Handlers;

// ─────────────────────────────────────────────────────────────────────────────
// PayMongo child-account onboarding (issue #91), phase 1.
//
// Sequenced so PayMongo is never billed before the driver has paid: the opening float must
// already be funded, the KYC fee is recovered from it, and only then is the account created.
// A driver who never funds their float never gets a PayMongo account and costs us nothing.
//
// Nothing here moves earnings or withdrawals. A driver whose wallet has no PayMongoAccountId
// stays on the original path untouched.
// ─────────────────────────────────────────────────────────────────────────────

public record StartPayMongoOnboardingCommand(Guid DriverId) : IRequest<Result<PayMongoOnboardingDto>>;

public record SubmitPayMongoOnboardingDetailsCommand(
    Guid DriverId,
    string? MiddleName,
    string Nationality,
    string NatureOfWork,
    string SourceOfFunds,
    string Tin,
    string PlaceOfBirthCity,
    string AddressLine1,
    string AddressCity,
    string AddressState,
    string AddressPostalCode,
    string? SourceOfFundsOther = null,
    // Falls back to the profile when omitted. Present so a driver whose profile has no phone can
    // supply one at setup rather than being stuck.
    string? MobileNumber = null) : IRequest<Result<PayMongoOnboardingDto>>;

public record GetPayMongoOnboardingStatusQuery(Guid DriverId) : IRequest<Result<PayMongoOnboardingDto>>;

/// <summary>
/// Opens the child account and hands back the hosted KYC link.
/// <para>
/// Idempotent by design: called again for a driver who already has an account, it re-issues a
/// verification session rather than opening a second one. A duplicate child account would be
/// permanent (PayMongo publishes no delete) and would split the driver's balance across two
/// wallets we could not reconcile.
/// </para>
/// </summary>
public class StartPayMongoOnboardingCommandHandler
    : IRequestHandler<StartPayMongoOnboardingCommand, Result<PayMongoOnboardingDto>>
{
    private readonly IDriverWalletRepository _repository;
    private readonly IPayMongoAccountsClient _accounts;
    private readonly DriverWalletOptions _options;
    private readonly ILogger<StartPayMongoOnboardingCommandHandler> _logger;
    private readonly UserManager<ApplicationUser>? _userManager;

    public StartPayMongoOnboardingCommandHandler(
        IDriverWalletRepository repository,
        IPayMongoAccountsClient accounts,
        IOptions<DriverWalletOptions> options,
        ILogger<StartPayMongoOnboardingCommandHandler> logger,
        UserManager<ApplicationUser>? userManager = null)
    {
        _repository = repository;
        _accounts = accounts;
        _options = options.Value;
        _logger = logger;
        _userManager = userManager;
    }

    public async Task<Result<PayMongoOnboardingDto>> Handle(
        StartPayMongoOnboardingCommand request, CancellationToken ct)
    {
        if (!_options.PayMongoOnboardingEnabled)
            return Result.Fail<PayMongoOnboardingDto>("Wallet setup is not available yet.");

        if (string.IsNullOrWhiteSpace(_options.PayMongoAccountEmailDomain))
        {
            // Refusing here rather than sending a placeholder: the address is frozen onto the
            // account at creation and cannot be corrected afterwards.
            _logger.LogError(
                "[PAYMONGO] [ONBOARDING] DriverWallet:PayMongoAccountEmailDomain is not configured; "
                + "cannot build a unique account email.");
            return Result.Fail<PayMongoOnboardingDto>("Wallet setup is misconfigured. Please contact support.");
        }

        var wallet = await _repository.GetWalletByDriverIdAsync(request.DriverId, ct);
        if (wallet is null)
            return Result.Fail<PayMongoOnboardingDto>("Driver wallet not found.");

        if (wallet.PayMongoActivationStatus == PayMongoActivationStatus.Declined)
            return Result.Fail<PayMongoOnboardingDto>(
                "Your wallet application was declined. Please contact support.");

        if (wallet.PayMongoActivationStatus == PayMongoActivationStatus.Activated)
            return Result.Ok(ToDto(wallet, verificationUrl: null));

        // The float must already cover the KYC fee. This is what keeps us from paying PayMongo
        // for a driver who has not paid us - see the issue's sequencing section.
        if (_options.AccountFeesEnabled && wallet.PayMongoAccountId is null &&
            wallet.TopUpBalance < _options.KycFeeAmount)
        {
            return Result.Fail<PayMongoOnboardingDto>(
                $"Add at least {_options.KycFeeAmount:N2} to your Cash Wallet before setting up BeeWallet.");
        }

        try
        {
            if (wallet.PayMongoAccountId is null)
            {
                // CLAIM BEFORE CALLING PAYMONGO.
                //
                // Creating a child account is irreversible - PayMongo publishes no delete, and it
                // may bill us for the KYC. Two concurrent requests (a double-tap, a retried POST)
                // would both read PayMongoAccountId as null and both create one, leaving an orphan
                // account nobody can remove.
                //
                // Writing the status first makes the wallet's xmin token the guard: the loser of
                // that race fails here, before anything exists at PayMongo.
                wallet.SetPayMongoActivationStatus(PayMongoActivationStatus.Pending);
                try
                {
                    await _repository.UpdateWalletAsync(wallet, ct);
                }
                catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
                {
                    _logger.LogInformation(
                        "[PAYMONGO] [ONBOARDING] Concurrent setup for driver {DriverId}; the other request wins",
                        request.DriverId);
                    return Result.Fail<PayMongoOnboardingDto>(
                        "Wallet setup is already in progress. Please wait a moment and try again.");
                }

                // Creation takes no person data - PayMongo discards it. Email, mobile and the rest
                // are supplied at activation instead.
                var account = await _accounts.CreateConsumerAccountAsync(ct);

                wallet.LinkPayMongoAccount(account.AccountId);

                // Fee and link committed together: a charge without a linked account would bill the
                // driver for nothing, and a link without the charge would give it away free.
                if (_options.AccountFeesEnabled)
                {
                    wallet.ChargeAccountFee(_options.KycFeeAmount, allowedNegativeLimit: 0m);
                    var fee = new WalletTransaction(
                        wallet.Id,
                        WalletTransactionType.AccountFee,
                        WalletBucket.TopUp,
                        // Positive, like every other transaction type. Direction is carried by
                        // WalletTransactionType, not by the sign - see CashSettlementDebit, the
                        // closest analogue. A negative here would be double-negated by any sum that
                        // subtracts debits by type, which is how GetCalculatedPersonalBalanceAsync
                        // works.
                        _options.KycFeeAmount,
                        WalletTransactionStatus.Completed,
                        "Wallet account setup fee",
                        // Keyed on the account id so a retry cannot charge the fee twice: the
                        // filtered unique index on (WalletId, Type, ProviderPaymentId) rejects it.
                        providerPaymentId: $"acctfee-kyc-{account.AccountId}");
                    await _repository.ApplyTransactionAsync(wallet, fee, ct);
                }
                else
                {
                    await _repository.UpdateWalletAsync(wallet, ct);
                }

                _logger.LogInformation(
                    "[PAYMONGO] [ONBOARDING] Linked driver {DriverId} to child account {AccountId}",
                    request.DriverId, account.AccountId);
            }

            var session = await _accounts.StartIdentityVerificationAsync(wallet.PayMongoAccountId!, ct);
            wallet.SetPayMongoActivationStatus(PayMongoActivationStatus.Verifying);
            await _repository.UpdateWalletAsync(wallet, ct);

            return Result.Ok(ToDto(wallet, session.Url, session.ExpiresAt));
        }
        catch (PaymentGatewayException ex)
        {
            _logger.LogError(ex,
                "[PAYMONGO] [ONBOARDING] Failed to start onboarding for driver {DriverId} (status {Status})",
                request.DriverId, ex.StatusCode);

            return Result.Fail<PayMongoOnboardingDto>(
                "Wallet setup is temporarily unavailable. Please try again later.");
        }
    }

    internal static PayMongoOnboardingDto ToDto(DriverWallet wallet, string? verificationUrl, DateTime? expiresAt = null)
        => new(
            wallet.PayMongoActivationStatus.ToString(),
            wallet.PayMongoAccountId,
            wallet.PayMongoAccountEmail,
            wallet.PayMongoAccountNumber,
            verificationUrl,
            expiresAt,
            wallet.PayMongoVerificationFailureReason,
            wallet.UsesPayMongoWallet);
}

/// <summary>
/// Fills the remaining person fields and activates, once identity verification has passed.
/// </summary>
public class SubmitPayMongoOnboardingDetailsCommandHandler
    : IRequestHandler<SubmitPayMongoOnboardingDetailsCommand, Result<PayMongoOnboardingDto>>
{
    private readonly IDriverWalletRepository _repository;
    private readonly IPayMongoAccountsClient _accounts;
    private readonly DriverWalletOptions _options;
    private readonly ILogger<SubmitPayMongoOnboardingDetailsCommandHandler> _logger;
    private readonly UserManager<ApplicationUser>? _userManager;

    public SubmitPayMongoOnboardingDetailsCommandHandler(
        IDriverWalletRepository repository,
        IPayMongoAccountsClient accounts,
        IOptions<DriverWalletOptions> options,
        ILogger<SubmitPayMongoOnboardingDetailsCommandHandler> logger,
        UserManager<ApplicationUser>? userManager = null)
    {
        _repository = repository;
        _accounts = accounts;
        _options = options.Value;
        _logger = logger;
        _userManager = userManager;
    }

    /// <summary>
    /// Addresses to try, in order of how well they reach the driver.
    /// <para>
    /// The plus-tagged variant is the useful middle rung: PayMongo treats it as a distinct address,
    /// but Gmail and most other providers deliver it to the same inbox — so the driver still gets
    /// PayMongo's notices and can still reset their password. The platform alias is last precisely
    /// because it reaches a mailbox they do not read.
    /// </para>
    /// </summary>
    /// <summary>
    /// A short, stable token identifying a driver inside an email tag.
    ///
    /// <para>
    /// Base36 over 6 characters (~2.2 billion values), derived deterministically from the driver id
    /// so a retry always produces the same address — a different one would open a second account.
    /// </para>
    /// <para>
    /// Deliberately not the full 32-character Guid: these appear in a driver's own inbox and in
    /// support conversations, and a collision is harmless anyway. Two drivers landing on the same
    /// token simply means the second gets a 409 and falls to the next rung of the ladder, which is
    /// the same path a driver with an existing PayMongo account already takes.
    /// </para>
    /// </summary>
    public static string ShortDriverToken(Guid driverId)
    {
        const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        const int Length = 6;

        var bytes = driverId.ToByteArray();
        ulong value = 0;
        for (var i = 0; i < 8; i++) value = (value << 8) | bytes[i];

        var chars = new char[Length];
        for (var i = Length - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value % 36)];
            value /= 36;
        }
        return new string(chars);
    }

    public static List<string> BuildEmailCandidates(string? driverEmail, Guid driverId, string aliasDomain)
    {
        var token = ShortDriverToken(driverId);
        var alias = $"driver+{token}@{aliasDomain}";
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(driverEmail))
        {
            var email = driverEmail!.Trim();
            candidates.Add(email);

            var at = email.IndexOf('@');
            if (at > 0)
            {
                var local = email[..at];
                var domain = email[(at + 1)..];
                // Replace any tag they already use rather than stacking a second one, which some
                // providers do not parse.
                var plus = local.IndexOf('+');
                if (plus > 0) local = local[..plus];

                var tagged = $"{local}+bee{token}@{domain}";
                if (!string.Equals(tagged, email, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(tagged);
            }
        }

        if (!string.IsNullOrWhiteSpace(aliasDomain))
            candidates.Add(alias);

        return candidates;
    }

    public async Task<Result<PayMongoOnboardingDto>> Handle(
        SubmitPayMongoOnboardingDetailsCommand request, CancellationToken ct)
    {
        if (!_options.PayMongoOnboardingEnabled)
            return Result.Fail<PayMongoOnboardingDto>("Wallet setup is not available yet.");

        var wallet = await _repository.GetWalletByDriverIdAsync(request.DriverId, ct);
        if (wallet?.PayMongoAccountId is null)
            return Result.Fail<PayMongoOnboardingDto>("Start wallet setup first.");

        if (wallet.PayMongoActivationStatus == PayMongoActivationStatus.Activated)
            return Result.Ok(StartPayMongoOnboardingCommandHandler.ToDto(wallet, verificationUrl: null));

        if (wallet.PayMongoActivationStatus == PayMongoActivationStatus.Declined)
            return Result.Fail<PayMongoOnboardingDto>(
                "Your wallet application was declined. Please contact support.");

        try
        {
            // Re-read rather than trusting our stored status: verification completes on PayMongo's
            // side and we may not have been told yet. Activating before it passes fails anyway, and
            // a failed activation is not recoverable on this account id.
            var remote = await _accounts.GetAccountAsync(wallet.PayMongoAccountId, ct);
            if (remote is null)
                return Result.Fail<PayMongoOnboardingDto>("Wallet setup could not be found. Please contact support.");

            if (!remote.IdentityVerificationPassed)
            {
                wallet.SetPayMongoActivationStatus(PayMongoActivationStatus.Verifying);
                await _repository.UpdateWalletAsync(wallet, ct);
                return Result.Fail<PayMongoOnboardingDto>("Finish identity verification first.");
            }

            var driverUser = _userManager is null
                ? null
                : await _userManager.FindByIdAsync(request.DriverId.ToString());

            // Activation requires a mobile number. Sending "" satisfies our own null check but not
            // PayMongo's, which rejects the ACTIVATE call - after the account is already created
            // and the KYC already paid for. Fail here, where it costs nothing and the driver can
            // act on it.
            var mobileNumber = string.IsNullOrWhiteSpace(request.MobileNumber)
                ? driverUser?.PhoneNumber
                : request.MobileNumber!.Trim();

            if (string.IsNullOrWhiteSpace(mobileNumber))
            {
                return Result.Fail<PayMongoOnboardingDto>(
                    "Add a mobile number to your profile before finishing wallet setup.");
            }

            // Checked against PayMongo's own list rather than a format guess: an invalid code fails
            // ACTIVATE, which is irreversible and freezes the account, so it must not get that far.
            if (!PhProvinces.IsValid(request.AddressState))
            {
                return Result.Fail<PayMongoOnboardingDto>(
                    "Select your province from the list — a valid Philippine province code is required.");
            }

            // PayMongo requires the address to be unique across EVERY account on its platform, and
            // freezes it at activation - so a driver who already has a PayMongo account cannot be
            // onboarded under their plain address, and there is no correcting it later.
            //
            // The ladder below prefers addresses that still reach the driver's real inbox:
            //   1. their own address
            //   2. a plus-tagged variant of it - unique to PayMongo, but delivered to the same
            //      mailbox by Gmail and most other providers
            //   3. a platform alias, only when they have no email on file or their provider does
            //      not carry plus-tags
            var candidates = BuildEmailCandidates(
                driverUser?.Email, request.DriverId, _options.PayMongoAccountEmailDomain);

            if (candidates.Count == 0)
            {
                _logger.LogError(
                    "[PAYMONGO] [ONBOARDING] Driver {DriverId} has no email on file and "
                    + "DriverWallet:PayMongoAccountEmailDomain is not configured; activation requires one.",
                    request.DriverId);
                return Result.Fail<PayMongoOnboardingDto>(
                    "Add an email address to your profile before setting up your wallet.");
            }

            var details = new UpdateChildAccountRequest(
                // Replaced per attempt by the candidate ladder below.
                EmailAddress: candidates[0],
                MobileNumber: mobileNumber ?? "",
                MiddleName: request.MiddleName,
                Nationality: request.Nationality,
                NatureOfWork: request.NatureOfWork,
                SourceOfFunds: request.SourceOfFunds,
                // Blank means "not provided", which is allowed: TIN is NOT in activation's required
                // set. Sending "" or a placeholder would put inaccurate data on a KYC record at a
                // regulated institution for no benefit.
                Tin: string.IsNullOrWhiteSpace(request.Tin) ? null : request.Tin!.Trim(),
                PlaceOfBirthCity: request.PlaceOfBirthCity,
                PlaceOfBirthCountry: "PH",
                AddressLine1: request.AddressLine1,
                AddressCity: request.AddressCity,
                AddressState: request.AddressState,
                AddressCountry: "PH",
                AddressPostalCode: request.AddressPostalCode,
                SourceOfFundsOther: request.SourceOfFundsOther);

            // Retries happen on the SAME child account, so a collision costs nothing and the
            // driver never sees it. Only the email varies between attempts.
            string? acceptedEmail = null;
            for (var i = 0; i < candidates.Count; i++)
            {
                try
                {
                    await _accounts.UpdateAccountAsync(
                        wallet.PayMongoAccountId, details with { EmailAddress = candidates[i] }, ct);
                    acceptedEmail = candidates[i];
                    break;
                }
                catch (PaymentGatewayException ex) when (ex.StatusCode == 409 && i < candidates.Count - 1)
                {
                    _logger.LogInformation(
                        "[PAYMONGO] [ONBOARDING] Email candidate {Index} for driver {DriverId} is already "
                        + "registered with PayMongo; trying the next",
                        i + 1, request.DriverId);
                }
            }

            if (acceptedEmail is not null)
                wallet.SetPayMongoAccountEmail(acceptedEmail);

            // Irreversible from here: activation freezes the account and a decline is terminal.
            var activated = await _accounts.ActivateAccountAsync(wallet.PayMongoAccountId, ct);

            if (activated.ActivationStatus == PayMongoActivationStatus.Declined)
            {
                wallet.SetPayMongoActivationStatus(PayMongoActivationStatus.Declined);
                await _repository.UpdateWalletAsync(wallet, ct);
                _logger.LogWarning(
                    "[PAYMONGO] [ONBOARDING] Child account {AccountId} for driver {DriverId} was declined",
                    wallet.PayMongoAccountId, request.DriverId);
                return Result.Fail<PayMongoOnboardingDto>(
                    "Your wallet application was declined. Please contact support.");
            }

            var walletInfo = await _accounts.GetWalletAsync(
                wallet.PayMongoAccountId, includeAccount: true, ct: ct);

            if (walletInfo is null || string.IsNullOrWhiteSpace(walletInfo.AccountNumber))
            {
                // Activated but we cannot address the wallet. Left as Verified rather than
                // Activated so nothing tries to pay into a wallet we have no account number for;
                // a retry picks it up.
                wallet.SetPayMongoActivationStatus(PayMongoActivationStatus.Verified);
                await _repository.UpdateWalletAsync(wallet, ct);
                _logger.LogError(
                    "[PAYMONGO] [ONBOARDING] Account {AccountId} activated but no wallet account number was returned",
                    wallet.PayMongoAccountId);
                return Result.Fail<PayMongoOnboardingDto>("Wallet setup is finishing. Please try again shortly.");
            }

            if (!string.Equals(walletInfo.Type, "default", StringComparison.OrdinalIgnoreCase))
            {
                // A closed-loop wallet can receive but never pay out. Better to notice now than to
                // tell a driver their earnings are withdrawable and have every transfer refused.
                _logger.LogWarning(
                    "[PAYMONGO] [ONBOARDING] Wallet {WalletId} for driver {DriverId} is type '{Type}', not 'default'; "
                    + "external payouts will be refused by PayMongo.",
                    walletInfo.WalletId, request.DriverId, walletInfo.Type);
            }

            wallet.SetPayMongoWallet(walletInfo.WalletId, walletInfo.AccountNumber, walletInfo.LedgerAccountId);
            await _repository.UpdateWalletAsync(wallet, ct);

            _logger.LogInformation(
                "[PAYMONGO] [ONBOARDING] Activated wallet {WalletId} for driver {DriverId}",
                walletInfo.WalletId, request.DriverId);

            return Result.Ok(StartPayMongoOnboardingCommandHandler.ToDto(wallet, verificationUrl: null));
        }
        catch (PaymentGatewayException ex)
        {
            _logger.LogError(ex,
                "[PAYMONGO] [ONBOARDING] Activation failed for driver {DriverId} (status {Status})",
                request.DriverId, ex.StatusCode);

            // 409 is the globally-unique-email rule. It means our alias has collided, which is an
            // operator problem the driver cannot resolve by retrying.
            return Result.Fail<PayMongoOnboardingDto>(ex.StatusCode == 409
                ? "This account cannot be set up automatically. Please contact support."
                : "Wallet setup is temporarily unavailable. Please try again later.");
        }
    }
}

public class GetPayMongoOnboardingStatusQueryHandler
    : IRequestHandler<GetPayMongoOnboardingStatusQuery, Result<PayMongoOnboardingDto>>
{
    private readonly IDriverWalletRepository _repository;

    public GetPayMongoOnboardingStatusQueryHandler(IDriverWalletRepository repository)
        => _repository = repository;

    public async Task<Result<PayMongoOnboardingDto>> Handle(
        GetPayMongoOnboardingStatusQuery request, CancellationToken ct)
    {
        var wallet = await _repository.GetWalletByDriverIdAsync(request.DriverId, ct);
        if (wallet is null)
            return Result.Fail<PayMongoOnboardingDto>("Driver wallet not found.");

        return Result.Ok(StartPayMongoOnboardingCommandHandler.ToDto(wallet, verificationUrl: null));
    }
}

public record SyncPayMongoAccountStatusCommand(
    string AccountId,
    string EventType,
    string? ActivationStatus,
    string? FailureReason = null) : IRequest<Result<bool>>;

/// <summary>
/// Applies a PayMongo child-account lifecycle webhook to the driver's wallet.
/// <para>
/// An accelerator, not the source of truth: these webhooks may not be enabled on every PayMongo
/// account, so the driver-triggered activate endpoint remains the guaranteed path. This handler
/// only moves the wallet forward when PayMongo tells us something we do not already know.
/// </para>
/// </summary>
public class SyncPayMongoAccountStatusCommandHandler
    : IRequestHandler<SyncPayMongoAccountStatusCommand, Result<bool>>
{
    private readonly IDriverWalletRepository _repository;
    private readonly IPayMongoAccountsClient _accounts;
    private readonly ILogger<SyncPayMongoAccountStatusCommandHandler> _logger;

    public SyncPayMongoAccountStatusCommandHandler(
        IDriverWalletRepository repository,
        IPayMongoAccountsClient accounts,
        ILogger<SyncPayMongoAccountStatusCommandHandler> logger)
    {
        _repository = repository;
        _accounts = accounts;
        _logger = logger;
    }

    public async Task<Result<bool>> Handle(SyncPayMongoAccountStatusCommand request, CancellationToken ct)
    {
        var wallet = await _repository.GetWalletByPayMongoAccountIdAsync(request.AccountId, ct);
        if (wallet is null)
        {
            // Not ours, or a webhook for an account we never linked. Returning success keeps
            // PayMongo from redelivering something we will never be able to act on.
            _logger.LogInformation(
                "[PAYMONGO] [ACCOUNT] No wallet linked to {AccountId}; ignoring {EventType}",
                request.AccountId, request.EventType);
            return Result.Ok(false);
        }

        // Declined is terminal on PayMongo's side too; the domain refuses to leave it, so stop here
        // rather than letting a late event throw.
        if (wallet.PayMongoActivationStatus == PayMongoActivationStatus.Declined)
            return Result.Ok(false);

        var evt = request.EventType.ToLowerInvariant();

        if (evt.EndsWith(".declined", StringComparison.Ordinal))
        {
            wallet.SetPayMongoActivationStatus(PayMongoActivationStatus.Declined);
            await _repository.UpdateWalletAsync(wallet, ct);
            _logger.LogWarning(
                "[PAYMONGO] [ACCOUNT] Child account {AccountId} declined for driver {DriverId}",
                request.AccountId, wallet.DriverId);
            return Result.Ok(true);
        }

        if (evt == "account.identity_verification.failed")
        {
            // Back to Pending, not a failure state: PayMongo's checks usually fail on something the
            // driver can fix - a blurry photo, poor light - so a fresh session is the right next
            // step. The reason is stored so we can tell them WHAT to fix rather than just "failed".
            wallet.SetPayMongoActivationStatus(PayMongoActivationStatus.Pending);
            wallet.SetPayMongoVerificationFailure(request.FailureReason);
            await _repository.UpdateWalletAsync(wallet, ct);
            _logger.LogInformation(
                "[PAYMONGO] [ACCOUNT] Identity verification failed for driver {DriverId}: {Reason}",
                wallet.DriverId, request.FailureReason ?? "no reason given");
            return Result.Ok(true);
        }

        if (evt == "account.identity_verification.passed")
        {
            if (wallet.PayMongoActivationStatus is PayMongoActivationStatus.Pending
                or PayMongoActivationStatus.Verifying)
            {
                wallet.SetPayMongoActivationStatus(PayMongoActivationStatus.Verified);
                // Clear any earlier failure, or the driver keeps seeing why a previous attempt
                // failed after the retry has already passed.
                wallet.SetPayMongoVerificationFailure(null);
                await _repository.UpdateWalletAsync(wallet, ct);
                return Result.Ok(true);
            }
            return Result.Ok(false);
        }

        if (evt.EndsWith(".activated", StringComparison.Ordinal))
        {
            if (wallet.UsesPayMongoWallet)
                return Result.Ok(false);

            // Activation provisions the wallet, but the event does not carry its account number,
            // and without that there is no destination to pay into. Fetch it before marking the
            // wallet usable rather than trusting the event alone.
            var walletInfo = await _accounts.GetWalletAsync(request.AccountId, includeAccount: true, ct: ct);
            if (walletInfo is null || string.IsNullOrWhiteSpace(walletInfo.AccountNumber))
            {
                wallet.SetPayMongoActivationStatus(PayMongoActivationStatus.Verified);
                await _repository.UpdateWalletAsync(wallet, ct);
                _logger.LogError(
                    "[PAYMONGO] [ACCOUNT] {AccountId} reported activated but no wallet account number "
                    + "was returned; leaving driver {DriverId} unactivated so nothing pays into it",
                    request.AccountId, wallet.DriverId);
                return Result.Ok(false);
            }

            wallet.SetPayMongoWallet(walletInfo.WalletId, walletInfo.AccountNumber, walletInfo.LedgerAccountId);
            await _repository.UpdateWalletAsync(wallet, ct);
            _logger.LogInformation(
                "[PAYMONGO] [ACCOUNT] Wallet {WalletId} activated for driver {DriverId} via webhook",
                walletInfo.WalletId, wallet.DriverId);
            return Result.Ok(true);
        }

        return Result.Ok(false);
    }
}

public record GetWithdrawableBalanceQuery(Guid DriverId) : IRequest<Result<WithdrawableBalanceDto>>;

/// <summary>
/// What a driver can actually withdraw right now.
/// <para>
/// Exists because "your balance" and "what you can withdraw" are not the same number once the money
/// is at PayMongo: the transfer fee comes out of the same wallet, so the maximum is always less
/// than the balance. Without this the app can only offer an amount that is certain to be rejected.
/// </para>
/// </summary>
public class GetWithdrawableBalanceQueryHandler
    : IRequestHandler<GetWithdrawableBalanceQuery, Result<WithdrawableBalanceDto>>
{
    private readonly IDriverWalletRepository _repository;
    private readonly Services.PayMongoWithdrawalService _withdrawals;
    private readonly DriverWalletOptions _options;

    public GetWithdrawableBalanceQueryHandler(
        IDriverWalletRepository repository,
        Services.PayMongoWithdrawalService withdrawals,
        IOptions<DriverWalletOptions> options)
    {
        _repository = repository;
        _withdrawals = withdrawals;
        _options = options.Value;
    }

    public async Task<Result<WithdrawableBalanceDto>> Handle(
        GetWithdrawableBalanceQuery request, CancellationToken ct)
    {
        var wallet = await _repository.GetWalletByDriverIdAsync(request.DriverId, ct);
        if (wallet is null)
            return Result.Fail<WithdrawableBalanceDto>("Driver wallet not found.");

        if (!_withdrawals.Handles(wallet))
        {
            // Unmigrated driver: the local balance is authoritative and we absorb the fee, so the
            // whole balance is withdrawable exactly as it is today.
            return Result.Ok(new WithdrawableBalanceDto(wallet.Balance, wallet.Balance, 0m, false));
        }

        var max = await _withdrawals.GetWithdrawableAsync(wallet, ct);
        if (!max.IsSuccess)
            return Result.Fail<WithdrawableBalanceDto>(max.Error);

        return Result.Ok(new WithdrawableBalanceDto(
            Balance: max.Value + _options.ExpectedWithdrawalFee,
            Withdrawable: max.Value,
            Fee: _options.ExpectedWithdrawalFee,
            FeePaidByDriver: true));
    }
}

/// <param name="Amount">
/// Fixes the amount in the code, so the payer cannot mistype it. Omit for the reusable QR the
/// driver can show anyone.
/// <para>
/// An amount makes the QR <b>dynamic</b>, which also makes it expire — PayMongo allows 60–9000s
/// and defaults to 1800. That is the trade: a static QR is one code forever, a dynamic one is good
/// for a single stated amount for half an hour.
/// </para>
/// </param>
public record GetBeeWalletTopUpQrQuery(Guid DriverId, decimal? Amount = null)
    : IRequest<Result<WalletTopUpQrDto>>;

/// <summary>
/// Returns a QR the driver scans to put money into their <b>own</b> BeeWallet wallet.
///
/// <para>
/// This is not a checkout. A checkout is a payment that only reaches a wallet on PayMongo's weekly
/// settlement run, which is why a top-up today shows the platform as the merchant and why we front
/// the driver's money until it lands. A QR Ph transfer settles in real time over InstaPay and
/// credits the driver's wallet directly.
/// </para>
/// <para>
/// Static, not dynamic: the QR belongs to the driver rather than to one payment, so it never
/// expires and they choose the amount when they scan. There is nothing to reconcile on our side —
/// the money is theirs, in their wallet, and we read the balance from PayMongo.
/// </para>
/// </summary>
public class GetBeeWalletTopUpQrQueryHandler
    : IRequestHandler<GetBeeWalletTopUpQrQuery, Result<WalletTopUpQrDto>>
{
    private readonly IDriverWalletRepository _repository;
    private readonly IPayMongoAccountsClient _accounts;
    private readonly ILogger<GetBeeWalletTopUpQrQueryHandler> _logger;

    public GetBeeWalletTopUpQrQueryHandler(
        IDriverWalletRepository repository,
        IPayMongoAccountsClient accounts,
        ILogger<GetBeeWalletTopUpQrQueryHandler> logger)
    {
        _repository = repository;
        _accounts = accounts;
        _logger = logger;
    }

    /// <summary>The bee mark composited into the middle of the QR, loaded once and reused.</summary>
    /// <remarks>
    /// Lazy rather than a static field initialiser: a missing or corrupt resource would otherwise
    /// throw inside a type initialiser, which surfaces as a TypeInitializationException from
    /// whatever happened to touch the class first and tells you nothing about the QR.
    /// </remarks>
    private static readonly Lazy<Image<Rgba32>?> BeeLogo = new(LoadBeeLogo);

    private static Image<Rgba32>? LoadBeeLogo()
    {
        try
        {
            using var stream = typeof(GetBeeWalletTopUpQrQueryHandler).Assembly
                .GetManifestResourceStream("BeeLogistics.Modules.Drivers.Application.Assets.bee-logo.png");
            return stream is null ? null : Image.Load<Rgba32>(stream);
        }
        catch
        {
            // A QR without a logo still pays the driver. Never fail the request over decoration.
            return null;
        }
    }

    /// <summary>
    /// Renders the EMV payload as a PNG data URI, with the bee mark in the middle.
    /// <para>
    /// Done here rather than in the app because the app has no QR renderer, and the obvious one
    /// (<c>react-native-svg</c>) carries native code — adding it would force a new dev/EAS build
    /// instead of a JavaScript-only update. The backend already depends on QRCoder, so this costs
    /// nothing and the app just shows an &lt;Image&gt;.
    /// </para>
    /// <para>
    /// <b>ECC level Q, and deliberately not H.</b> Raising it to carry the logo is the obvious
    /// move and it is the wrong one: H needs 73 modules for this payload against Q's 65, so every
    /// module is smaller at the same rendered size and the code gets <i>harder</i> to read — an H
    /// code stopped scanning below ~240px where the Q code kept reading to ~160px. The logo costs
    /// almost nothing; the extra density costs real scan range. Measured, not assumed.
    /// </para>
    /// </summary>
    internal static string RenderQrPng(string payload)
    {
        using var generator = new QRCodeGenerator();
        var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        using var png = new PngByteQRCode(data);
        var qrBytes = png.GetGraphic(10);

        var logo = BeeLogo.Value;
        if (logo is null)
            return "data:image/png;base64," + Convert.ToBase64String(qrBytes);

        try
        {
            using var qr = Image.Load<Rgba32>(qrBytes);

            // 20% of the width, and the margin is larger than it looks: a spec-grade decoder
            // (ZXing) still reads this payload with a 32% plate, so error correction is nowhere
            // near its limit here. The reason not to take that headroom is that real scanners are
            // not spec-grade — OpenCV's detector, which is closer to what a mid-range phone ships,
            // starts losing codes above ~20% once they are also shrunk or blurred. Sized for the
            // pickier reader, not the best one.
            var plateSize = (int)(qr.Width * 0.20);
            var logoSize = (int)(plateSize * 0.78);   // padding, so modules never touch the mark

            using var mark = logo.Clone(c => c.Resize(logoSize, logoSize));

            // An opaque white plate under the mark. Without it the black bee merges into the black
            // modules around it and reads as an unrecognisable blob, both to a human and to a
            // decoder trying to find the module grid.
            using var plate = new Image<Rgba32>(plateSize, plateSize, Color.White.ToPixel<Rgba32>());

            var plateAt = new Point((qr.Width - plateSize) / 2, (qr.Height - plateSize) / 2);
            var markAt = new Point((qr.Width - logoSize) / 2, (qr.Height - logoSize) / 2);

            qr.Mutate(c => c.DrawImage(plate, plateAt, 1f).DrawImage(mark, markAt, 1f));

            using var output = new MemoryStream();
            qr.SaveAsPng(output);
            return "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
        }
        catch
        {
            // Same reasoning as a missing logo: fall back to the plain code rather than denying
            // the driver a way to get paid.
            return "data:image/png;base64," + Convert.ToBase64String(qrBytes);
        }
    }

    public async Task<Result<WalletTopUpQrDto>> Handle(GetBeeWalletTopUpQrQuery request, CancellationToken ct)
    {
        var wallet = await _repository.GetWalletByDriverIdAsync(request.DriverId, ct);
        if (wallet is null)
            return Result.Fail<WalletTopUpQrDto>("Driver wallet not found.");

        if (!wallet.UsesPayMongoWallet)
        {
            // No wallet of their own to fund yet. Offering a QR here would generate the PLATFORM's,
            // which is exactly the confusion this replaces.
            return Result.Fail<WalletTopUpQrDto>(
                "Finish setting up BeeWallet before adding money to it.");
        }

        // Guarded here rather than left to PayMongo: the client throws ArgumentException on a
        // non-positive dynamic amount, which would surface as a 500 instead of something the driver
        // can act on.
        if (request.Amount is <= 0m)
            return Result.Fail<WalletTopUpQrDto>("Enter an amount greater than zero.");

        // Rejected before the call for the same reason. QR Ph carries the amount in the payload, so
        // a value with sub-centavo precision produces a code that either fails to parse or silently
        // rounds - neither of which the driver would be able to explain.
        if (request.Amount is { } requested && decimal.Round(requested, 2) != requested)
            return Result.Fail<WalletTopUpQrDto>("Amounts can have at most two decimal places.");

        try
        {
            // An amount makes it dynamic, and a dynamic QR expires. Without one it stays the
            // reusable code the driver can keep showing.
            var qr = await _accounts.GenerateWalletQrAsync(new GenerateWalletQrRequest(
                OnBehalfOfAccountId: wallet.PayMongoAccountId,
                Mode: WalletQrMode.P2P,
                Type: request.Amount is null ? WalletQrType.Static : WalletQrType.Dynamic,
                Amount: request.Amount), ct);

            // Assert the QR credits THIS driver. Without Account-Id the endpoint returns the
            // platform's QR, and a driver scanning it would be paying us instead of themselves -
            // silently, because the code would look identical.
            if (!string.Equals(qr.CreditAccountNumber, wallet.PayMongoAccountNumber, StringComparison.Ordinal))
            {
                _logger.LogCritical(
                    "[PAYMONGO] [QR] Generated QR credits {Actual} but driver {DriverId} owns {Expected}; refusing to show it.",
                    qr.CreditAccountNumber ?? "n/a", request.DriverId, wallet.PayMongoAccountNumber);
                return Result.Fail<WalletTopUpQrDto>("Could not create your QR code. Please try again.");
            }

            return Result.Ok(new WalletTopUpQrDto(
                qr.QrString,
                RenderQrPng(qr.QrString),
                qr.MerchantName,
                qr.CreditAccountNumber,
                qr.ExpiresAt,
                request.Amount));
        }
        catch (PaymentGatewayException ex)
        {
            _logger.LogError(ex,
                "[PAYMONGO] [QR] Could not generate a BeeWallet QR for driver {DriverId}", request.DriverId);
            return Result.Fail<WalletTopUpQrDto>("Could not create your QR code. Please try again.");
        }
    }
}

/// <param name="IdempotencyKey">
/// Unique per payment. Must be the transfer id, never the QR id — a static BeeWallet QR keeps one id
/// for every payment it ever receives.
/// </param>
public record CreditBeeWalletTopUpCommand(
    string? CreditAccountNumber,
    string? AccountId,
    decimal Amount,
    string IdempotencyKey) : IRequest<Result<bool>>;

/// <summary>
/// Credits the local mirror after someone scanned a driver's BeeWallet QR and PayMongo settled the
/// money into their child wallet.
/// </summary>
/// <remarks>
/// Unlike a checkout top-up, there is nothing to reserve or confirm: the money is already in the
/// driver's wallet by the time this webhook arrives — QR Ph settles real time over InstaPay — so
/// the only job here is to stop our copy of the balance from lagging behind PayMongo's.
/// </remarks>
public class CreditBeeWalletTopUpCommandHandler : IRequestHandler<CreditBeeWalletTopUpCommand, Result<bool>>
{
    private readonly IDriverWalletRepository _repository;
    private readonly ILogger<CreditBeeWalletTopUpCommandHandler> _logger;

    public CreditBeeWalletTopUpCommandHandler(
        IDriverWalletRepository repository,
        ILogger<CreditBeeWalletTopUpCommandHandler> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<Result<bool>> Handle(CreditBeeWalletTopUpCommand request, CancellationToken ct)
    {
        if (request.Amount <= 0)
            return Result.Fail<bool>("Amount must be positive");

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return Result.Fail<bool>("An idempotency key is required to credit a BeeWallet top-up");

        // Account number first: it is what we store verbatim off the wallet, so it needs no
        // normalising. The account id is the fallback for payloads that omit it.
        DriverWallet? wallet = null;
        if (!string.IsNullOrWhiteSpace(request.CreditAccountNumber))
            wallet = await _repository.GetWalletByPayMongoAccountNumberAsync(request.CreditAccountNumber!, ct);

        if (wallet is null && !string.IsNullOrWhiteSpace(request.AccountId))
            wallet = await _repository.GetWalletByPayMongoAccountIdAsync(request.AccountId!, ct);

        if (wallet is null)
        {
            // A QR paid on the platform's own wallet rather than a driver's reaches us too. There
            // is no driver mirror to move, so acknowledge instead of making PayMongo redeliver.
            _logger.LogInformation(
                "[PAYMONGO] [QR] Paid QR matched no driver wallet (account {AccountNumber} / {AccountId}); ignoring",
                request.CreditAccountNumber ?? "n/a", request.AccountId ?? "n/a");
            return Result.Ok(false);
        }

        if (await _repository.HasTransactionForProviderPaymentAsync(
                wallet.Id, request.IdempotencyKey, WalletTransactionType.TopUp, ct))
        {
            _logger.LogInformation(
                "[PAYMONGO] [QR] Payment {Key} already credited to driver {DriverId}",
                request.IdempotencyKey, wallet.DriverId);
            return Result.Ok(false);
        }

        var transaction = new WalletTransaction(
            wallet.Id,
            WalletTransactionType.TopUp,
            // Personal, not TopUp: a BeeWallet QR pays into the driver's own PayMongo wallet, which is
            // the withdrawable side. The Cash Wallet float is funded through checkout instead.
            WalletBucket.Personal,
            request.Amount,
            WalletTransactionStatus.Completed,
            "Top-up",
            providerPaymentId: request.IdempotencyKey);

        wallet.AddBeeWalletTopUp(request.Amount);

        try
        {
            await _repository.ApplyTransactionAsync(wallet, transaction, ct);
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            // A concurrent redelivery won. The check above is the ordinary guard; the unique index
            // on (WalletId, Type, ProviderPaymentId) is the one that actually holds under a race.
            _logger.LogInformation(
                "[PAYMONGO] [QR] Payment {Key} credited concurrently for driver {DriverId}",
                request.IdempotencyKey, wallet.DriverId);
            return Result.Ok(false);
        }

        _logger.LogInformation(
            "[PAYMONGO] [QR] Credited {Amount} to driver {DriverId} BeeWallet from payment {Key}",
            request.Amount, wallet.DriverId, request.IdempotencyKey);

        return Result.Ok(true);
    }

    private static bool IsDuplicate(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) == true;
}
