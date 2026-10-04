using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Banks;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Drivers.Application.Services;

/// <summary>
/// Phase 3: a withdrawal that leaves the <b>driver's own</b> PayMongo wallet rather than ours
/// (issue #91).
///
/// <para>
/// Most of what <c>RequestWithdrawalCommandHandler</c> does has no counterpart here. There is no
/// reserve-then-send ordering, no <c>xmin</c> race to lose and no refund-on-failure, because we are
/// not the custodian: PayMongo holds the balance and rejects an overdraw itself. What replaces all
/// of it is one rule — <b>never ask for more than the wallet can cover including the fee</b>.
/// </para>
/// </summary>
public class PayMongoWithdrawalService
{
    private readonly IDriverWalletRepository _repository;
    private readonly IPayMongoAccountsClient _accounts;
    private readonly DriverWalletOptions _options;
    private readonly ILogger<PayMongoWithdrawalService> _logger;

    public PayMongoWithdrawalService(
        IDriverWalletRepository repository,
        IPayMongoAccountsClient accounts,
        IOptions<DriverWalletOptions> options,
        ILogger<PayMongoWithdrawalService> logger)
    {
        _repository = repository;
        _accounts = accounts;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>True when this driver's withdrawals should leave their own PayMongo wallet.</summary>
    public bool Handles(DriverWallet wallet)
        => _options.PayMongoWithdrawalsEnabled && wallet.UsesPayMongoWallet;

    /// <summary>
    /// The most this driver can withdraw right now: their PayMongo balance less the fee PayMongo
    /// will take from the same wallet. Surfaced so the app can cap the field rather than letting
    /// the driver submit an amount that is certain to fail.
    /// </summary>
    public async Task<Result<decimal>> GetWithdrawableAsync(DriverWallet wallet, CancellationToken ct = default)
    {
        var remote = await _accounts.GetWalletAsync(wallet.PayMongoAccountId!, includeBalance: true, ct: ct);
        if (remote?.AvailableBalance is not { } available)
            return Result.Fail<decimal>("Could not read your wallet balance. Please try again.");

        var withdrawable = available - _options.ExpectedWithdrawalFee;
        return Result.Ok(withdrawable > 0m ? withdrawable : 0m);
    }

    public async Task<Result<WithdrawalRequest>> WithdrawAsync(
        DriverWallet wallet,
        decimal amount,
        string bankCode,
        string destinationAccountNumber,
        string destinationAccountName,
        string? idempotencyKey,
        CancellationToken ct = default)
    {
        if (amount <= 0)
            return Result.Fail<WithdrawalRequest>("Enter an amount greater than zero.");

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var replay = await _repository.GetWithdrawalByDriverAndIdempotencyKeyAsync(
                wallet.DriverId, idempotencyKey!.Trim(), ct);
            if (replay is not null) return Result.Ok(replay);
        }

        if (!PhBankCatalog.TryResolve(bankCode, out var bank) || bank is null)
            return Result.Fail<WithdrawalRequest>(
                "That bank isn't supported for withdrawals. Please pick another from the list.");

        var (rail, rejection) = WithdrawalRailSelector.Select(bank, amount);
        if (rejection is not null || rail is null)
            return Result.Fail<WithdrawalRequest>(rejection!.Reason);

        // PayMongo's balance, not our mirror. The mirror can drift, and letting a stale figure
        // authorise a transfer is exactly the class of bug the mirror exists to detect, not cause.
        var remote = await _accounts.GetWalletAsync(wallet.PayMongoAccountId!, includeBalance: true, ct: ct);
        if (remote?.AvailableBalance is not { } available)
            return Result.Fail<WithdrawalRequest>("Could not read your wallet balance. Please try again.");

        // The fee comes out of the same wallet, so "withdraw everything" needs headroom for it.
        // Without this the request is accepted and then fails at PayMongo with
        // "source_account_balance: insufficient", which reads to a driver as a broken app.
        var required = amount + _options.ExpectedWithdrawalFee;
        if (required > available)
        {
            var max = available - _options.ExpectedWithdrawalFee;
            return Result.Fail<WithdrawalRequest>(max > 0m
                ? $"You can withdraw up to ₱{max:N2}. A ₱{_options.ExpectedWithdrawalFee:N2} transfer fee "
                  + "is taken from your wallet on top of the amount."
                : $"Your balance doesn't cover the ₱{_options.ExpectedWithdrawalFee:N2} transfer fee yet.");
        }

        var withdrawal = new WithdrawalRequest(
            wallet.DriverId, wallet.Id, amount,
            destinationAccountNumber, bank.Name, destinationAccountName,
            // Starts Pending, not Approved: it is only approved once PayMongo has actually taken
            // the transfer. Recording it as approved up front would leave a rejected withdrawal
            // sitting in the driver's history as though it had gone through.
            autoApprove: false, idempotencyKey: idempotencyKey);

        await _repository.CreateWithdrawalRequestAsync(withdrawal, ct);

        try
        {
            var result = await _accounts.WithdrawFromChildAsync(new ChildWalletPayoutRequest(
                AccountId: wallet.PayMongoAccountId!,
                SourceAccountNumber: wallet.PayMongoAccountNumber!,
                SourceAccountName: wallet.AccountHolderName ?? destinationAccountName,
                DestinationAccountNumber: destinationAccountNumber,
                DestinationAccountName: destinationAccountName,
                DestinationBic: rail.Bic,
                Provider: rail.Rail == TransferRail.Instapay ? "instapay" : "pesonet",
                Amount: amount,
                ReferenceNumber: withdrawal.Id.ToString("N"),
                Description: "Driver withdrawal"), ct);

            if (result.Failed)
            {
                withdrawal.MarkAsFailed(result.ProviderErrorMessage ?? "PayMongo rejected the transfer.");
                await _repository.UpdateWithdrawalRequestAsync(withdrawal, ct);
                _logger.LogWarning(
                    "[PAYMONGO] [WITHDRAWAL] Driver {DriverId} withdrawal of {Amount} failed: {Reason}",
                    wallet.DriverId, amount, result.ProviderErrorMessage ?? "no reason given");
                return Result.Fail<WithdrawalRequest>(
                    "Your withdrawal could not be completed. Please check the account details and try again.");
            }

            withdrawal.SetProviderDisbursement(PaymentProviders.PayMongo, result.TransferId);
            withdrawal.AutoApprove();
            // Recorded here because this is the only moment the fee is in hand: the settle paths
            // run later, from the stored withdrawal, with no transfer to read it off.
            withdrawal.RecordProviderFee(result.Fee);
            await _repository.UpdateWithdrawalRequestAsync(withdrawal, ct);

            _logger.LogInformation(
                "[PAYMONGO] [WITHDRAWAL] Driver {DriverId} withdrew {Amount} over {Rail} ({TransferId}, fee {Fee})",
                wallet.DriverId, amount, rail.Rail, result.TransferId, result.Fee);

            return Result.Ok(withdrawal);
        }
        catch (PaymentGatewayException ex)
        {
            // No local balance was reserved, so there is nothing to refund - the money either left
            // the driver's PayMongo wallet or it did not. Record the failure and let the driver
            // retry.
            withdrawal.MarkAsFailed(ex.Message);
            await _repository.UpdateWithdrawalRequestAsync(withdrawal, ct);

            _logger.LogError(ex,
                "[PAYMONGO] [WITHDRAWAL] Driver {DriverId} withdrawal of {Amount} was rejected (status {Status})",
                wallet.DriverId, amount, ex.StatusCode);

            return Result.Fail<WithdrawalRequest>(ex.StatusCode is >= 400 and < 500
                ? "Your withdrawal was rejected. Please check the account details and try again."
                : "Withdrawals are temporarily unavailable. Please try again later.");
        }
    }
}
