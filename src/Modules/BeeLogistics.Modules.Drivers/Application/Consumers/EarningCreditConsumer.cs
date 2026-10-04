using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Modules.Revenue.Application.Interfaces;
using BeeLogistics.Modules.Revenue.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Drivers.Application.Consumers;

/// <summary>
/// Listens for BookingCompletedEvent and:
/// 1. Credits 95% of the fare to the driver's Personal wallet (for cashless payments)
/// 2. Records a PlatformCommission ledger entry (5% for the platform)
///
/// Cash deliveries:
///   - The driver already collected the full amount from the customer.
///   - CashDeliverySettlementConsumer debits the platform's 5% from the TopUp wallet.
///   - This consumer still records the PlatformCommission ledger entry for accounting.
///
/// Cashless deliveries:
///   - Xendit holds the full payment.
///   - This consumer credits 95% to the driver's Personal wallet.
///   - The remaining 5% stays on the platform (Xendit → platform account).
///
/// Retry behaviour:
///   - If the payment exists but is not yet Paid (webhook hasn't arrived), this consumer
///     throws so MassTransit retries. After all retries are exhausted, it falls back to
///     the booking's FinalFare from the event to credit the driver.
/// </summary>
public class EarningCreditConsumer : IConsumer<BookingCompletedEvent>
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly IDriverWalletRepository _walletRepository;
    private readonly IPlatformCommissionRepository _commissionRepository;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly DriverWalletOptions _options;
    private readonly ILogger<EarningCreditConsumer> _logger;

    /// <summary>
    /// Maximum number of times we retry before falling back to FinalFare.
    /// MassTransit's RetryCount on the ConsumeContext tells us how many retries have occurred.
    /// </summary>
    private const int MaxRetriesBeforeFallback = 5;

    private readonly IPayMongoAccountsClient? _payMongoAccounts;

    public EarningCreditConsumer(
        IPaymentRepository paymentRepository,
        IDriverWalletRepository walletRepository,
        IPlatformCommissionRepository commissionRepository,
        IPublishEndpoint publishEndpoint,
        IOptions<DriverWalletOptions> options,
        ILogger<EarningCreditConsumer> logger,
        IPayMongoAccountsClient? payMongoAccounts = null)
    {
        _payMongoAccounts = payMongoAccounts;
        _paymentRepository = paymentRepository;
        _walletRepository = walletRepository;
        _commissionRepository = commissionRepository;
        _publishEndpoint = publishEndpoint;
        _options = options.Value;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<BookingCompletedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        // NOTE: there is deliberately no "commission exists, therefore skip everything" guard here.
        // The commission (RevenueDbContext) and the wallet credit (DriversDbContext) live in
        // different contexts, so they cannot be committed together. A single top-level marker
        // meant that a failure after the commission was written but before the driver was paid
        // caused the retry to short-circuit - and the driver was never credited at all.
        //
        // Instead every step below guards itself, so a retry re-enters and completes whichever
        // steps are still missing rather than assuming the whole unit of work is done.

        // Get the payment for this booking
        var payment = await _paymentRepository.GetByBookingIdAsync(msg.BookingId, ct);
        var retryCount = context.GetRetryCount();

        // --- Handle missing or not-yet-paid payment ---
        if (payment == null || payment.Status != PaymentStatus.Paid)
        {
            // If we haven't exhausted retries, throw to trigger MassTransit retry.
            // This gives time for the Xendit webhook to arrive and mark the payment as Paid.
            if (retryCount < MaxRetriesBeforeFallback)
            {
                _logger.LogWarning(
                    "Payment not yet Paid for booking {BookingId} (status: {Status}, retry: {Retry}/{Max}). " +
                    "Throwing to trigger retry.",
                    msg.BookingId,
                    payment?.Status.ToString() ?? "NOT_FOUND",
                    retryCount,
                    MaxRetriesBeforeFallback);

                throw new InvalidOperationException(
                    $"Payment for booking {msg.BookingId} is not yet Paid (status: {payment?.Status.ToString() ?? "NOT_FOUND"}). " +
                    $"Retry {retryCount}/{MaxRetriesBeforeFallback}.");
            }

            // Retries exhausted — fall back to FinalFare from the booking event
            if (!msg.FinalFare.HasValue || msg.FinalFare.Value <= 0)
            {
                _logger.LogError(
                    "Payment not found/not Paid for booking {BookingId} after {Max} retries and no FinalFare available. " +
                    "Cannot credit earnings. Manual intervention required.",
                    msg.BookingId, MaxRetriesBeforeFallback);
                return;
            }

            _logger.LogWarning(
                "Payment not found/not Paid for booking {BookingId} after {Max} retries. " +
                "Falling back to FinalFare {FinalFare} from booking event.",
                msg.BookingId, MaxRetriesBeforeFallback, msg.FinalFare.Value);

            await CreditEarningsFromFare(msg, msg.FinalFare.Value, paymentMethod: "Fallback", isCash: false, ct);
            return;
        }

        // --- Normal path: payment exists and is Paid ---
        var grossAmount = payment.Amount;
        var isCash = payment.Method == PaymentMethod.Cash;

        await CreditEarningsFromFare(msg, grossAmount, payment.Method.ToString(), isCash, ct);
    }

    private async Task CreditEarningsFromFare(
        BookingCompletedEvent msg,
        decimal grossAmount,
        string paymentMethod,
        bool isCash,
        CancellationToken ct)
    {
        var commissionRate = _options.PlatformCommissionRate;

        // --- 1. Record the PlatformCommission ledger entry (for ALL payment methods) ---
        // Guarded individually rather than by a single marker at the top of Consume, so a retry
        // after a partial failure fills in what is missing instead of skipping the rest.
        var commission = await _commissionRepository.GetByBookingIdAsync(msg.BookingId, ct);
        if (commission == null)
        {
            commission = new PlatformCommission(
                msg.BookingId,
                msg.DriverId,
                paymentMethod,
                grossAmount,
                commissionRate);

            await _commissionRepository.CreateAsync(commission, ct);

            _logger.LogInformation(
                "Recorded platform commission for booking {BookingId}: gross={Gross}, commission={Commission} ({Rate}%), driver={Driver}, method={Method}",
                msg.BookingId, grossAmount, commission.CommissionAmount, commissionRate * 100, commission.DriverAmount, paymentMethod);
        }

        // Taken from the commission record rather than recomputed. Deriving it here separately
        // produced a value that disagreed with the commission by a centavo on ordinary fares,
        // so the wallet credit, the commission row and the accounting ledger all disagreed and
        // the double-entry set did not balance. See EarningsSplit.
        var driverAmount = commission.DriverAmount;

        // --- 2. Record the driver's earning ---
        var wallet = await _walletRepository.GetWalletByDriverIdAsync(msg.DriverId, ct);
        if (wallet == null)
        {
            wallet = new DriverWallet(msg.DriverId);
            wallet = await _walletRepository.CreateWalletAsync(wallet, ct);
        }

        // Keyed on the booking id, not on a formatted description. The cash branch used to look
        // up one string and store a different (longer) one, so the guard never matched and every
        // redelivery appended another row. Backed by the unique index on
        // (WalletId, RelatedBookingId, Type).
        var alreadyRecorded = await _walletRepository.HasTransactionForBookingAsync(
            wallet.Id, msg.BookingId, WalletTransactionType.Earning, ct);

        if (!alreadyRecorded)
        {
            if (isCash)
            {
                // Cash: the driver already holds the money, so this is history only - no balance
                // change. IsCashEarning keeps it out of the withdrawable balance calculation.
                var breakdownDescription = $"Earning from booking {msg.BookingId} (cash). Fare: ₱{grossAmount:N2}, Platform (5%): ₱{commission.CommissionAmount:N2}, Net: ₱{driverAmount:N2}";
                var cashTransaction = new WalletTransaction(
                    wallet.Id,
                    WalletTransactionType.Earning,
                    WalletBucket.Personal,
                    driverAmount,
                    WalletTransactionStatus.Completed,
                    breakdownDescription,
                    msg.BookingId,
                    isCashEarning: true);

                await _walletRepository.CreateTransactionAsync(cashTransaction, ct);

                _logger.LogInformation(
                    "Recorded cash earning history for booking {BookingId}, driver {DriverId} (no balance change).",
                    msg.BookingId, msg.DriverId);
            }
            else
            {
                // Drivers on the PayMongo child-wallet path (issue #91) hold their withdrawable
                // balance there, not here, so their share is pushed into their own wallet. Everyone
                // else takes the original path unchanged - UsesPayMongoWallet is false for them.
                var pushToChildWallet =
                    _options.PayMongoEarningsPushEnabled &&
                    _payMongoAccounts is not null &&
                    wallet.UsesPayMongoWallet;

                if (pushToChildWallet)
                {
                    // CLAIM BEFORE SENDING (the issue #90 ordering, applied here).
                    //
                    // The transfer is irreversible; writing the row afterwards would mean a crash
                    // or a lost save between the two leaves money in the driver's wallet that our
                    // records do not know about - and the retry, finding no row, would send it
                    // AGAIN. Whether PayMongo rejects a reused reference_number is unverified (a
                    // doomed transfer fails balance validation before any duplicate check, so it
                    // cannot be tested cheaply), so nothing here may depend on it.
                    //
                    // Writing the row first makes the unique index on (WalletId, RelatedBookingId,
                    // Type) the guard instead: a concurrent or redelivered attempt collides here,
                    // before any money moves.
                    var claim = new WalletTransaction(
                        wallet.Id,
                        WalletTransactionType.Earning,
                        WalletBucket.Personal,
                        driverAmount,
                        WalletTransactionStatus.Pending,
                        $"Earning from booking {msg.BookingId}",
                        msg.BookingId);

                    await _walletRepository.CreateTransactionAsync(claim, ct);

                    var reference = $"earn-{msg.BookingId:N}";
                    var transfer = await _payMongoAccounts!.TransferToChildAsync(
                        wallet.PayMongoAccountId!,
                        wallet.PayMongoAccountNumber!,
                        wallet.AccountHolderName ?? "Driver",
                        driverAmount,
                        reference,
                        $"Earning from booking {msg.BookingId}",
                        ct);

                    if (transfer.Failed)
                    {
                        // The money never left our wallet, so the mirror must not move. The claim
                        // is marked failed rather than deleted, so a retry sees a definite outcome
                        // instead of an ambiguous gap.
                        claim.MarkAsFailed();
                        await _walletRepository.UpdateTransactionAsync(claim, ct);

                        _logger.LogError(
                            "[PAYMONGO] [EARNINGS] Transfer to driver {DriverId} wallet failed for booking "
                            + "{BookingId}: {Reason}",
                            msg.DriverId, msg.BookingId, transfer.ProviderErrorMessage ?? "no reason given");
                        throw new InvalidOperationException(
                            $"PayMongo rejected the earnings transfer for booking {msg.BookingId}.");
                    }

                    // The mirror follows the money rather than replacing it. PayMongo is
                    // authoritative for a migrated driver; this row is what the app reads and what
                    // reconciliation checks against.
                    wallet.AddEarning(driverAmount);
                    claim.MarkAsCompleted();
                    claim.SetProviderPaymentId(transfer.TransferId);

                    await _walletRepository.SaveClaimedTransactionAsync(wallet, claim, ct);

                    _logger.LogInformation(
                        "[PAYMONGO] [EARNINGS] Paid {Amount} into driver {DriverId} wallet for booking "
                        + "{BookingId} ({TransferId}, fee {Fee})",
                        driverAmount, msg.DriverId, msg.BookingId, transfer.TransferId, transfer.Fee);
                }
                else
                {
                    wallet.AddEarning(driverAmount);

                    var transaction = new WalletTransaction(
                        wallet.Id,
                        WalletTransactionType.Earning,
                        WalletBucket.Personal,
                        driverAmount,
                        WalletTransactionStatus.Completed,
                        $"Earning from booking {msg.BookingId}",
                        msg.BookingId);

                    // Row and balance in one SaveChanges: committing them separately could leave a
                    // transaction row with no balance change, which the guard above then mistook for
                    // completed work.
                    await _walletRepository.ApplyTransactionAsync(wallet, transaction, ct);

                    _logger.LogInformation(
                        "Credited {Amount} to driver {DriverId} Personal wallet for cashless booking {BookingId}",
                        driverAmount, msg.DriverId, msg.BookingId);
                }
            }
        }

        // --- 3. Publish for accounting (sales fast table + ledger) ---
        // Last, so it only announces work that actually landed. The accounting consumer is
        // idempotent by BookingId, so a redelivery republishing this is harmless.
        var accountingPaymentMethod = isCash ? "Cash" : "Cashless";
        await _publishEndpoint.Publish(new SaleRecordedEvent(
            msg.BookingId,
            msg.DriverId,
            msg.CustomerId,
            grossAmount,
            accountingPaymentMethod,
            commission.CommissionAmount,
            driverAmount,
            msg.CompletedAt
        ), ct);
    }
}
