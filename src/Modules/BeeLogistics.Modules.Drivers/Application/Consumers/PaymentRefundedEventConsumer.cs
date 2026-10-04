using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Revenue.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Drivers.Application.Consumers;

/// <summary>
/// When a cashless payment is refunded (PaymentRefundedEvent), debits the driver's Personal wallet
/// only if the driver was previously credited for this booking (delivery had completed). Idempotent.
/// </summary>
public class PaymentRefundedEventConsumer : IConsumer<PaymentRefundedEvent>
{
    private readonly IDriverWalletRepository _walletRepository;
    private readonly DriverWalletOptions _options;
    private readonly ILogger<PaymentRefundedEventConsumer> _logger;

    public PaymentRefundedEventConsumer(
        IDriverWalletRepository walletRepository,
        IOptions<DriverWalletOptions> options,
        ILogger<PaymentRefundedEventConsumer> logger)
    {
        _walletRepository = walletRepository;
        _options = options.Value;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PaymentRefundedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var wallet = await _walletRepository.GetWalletByDriverIdAsync(msg.DriverId, ct);
        if (wallet == null)
        {
            _logger.LogDebug("No wallet for driver {DriverId}, skipping refund reversal.", msg.DriverId);
            return;
        }

        var wasCredited = await _walletRepository.HasTransactionForBookingAsync(
            wallet.Id, msg.BookingId, WalletTransactionType.Earning, ct);
        if (!wasCredited)
        {
            _logger.LogDebug(
                "Driver {DriverId} was not credited for booking {BookingId} (cancelled before completion), skipping reversal.",
                msg.DriverId, msg.BookingId);
            return;
        }

        var reversalDescription = $"Refund for booking {msg.BookingId}";
        var alreadyReversed = await _walletRepository.HasTransactionForBookingAsync(
            wallet.Id, msg.BookingId, WalletTransactionType.EarningReversal, ct);
        if (alreadyReversed)
        {
            _logger.LogDebug("Refund reversal already applied for booking {BookingId}, skipping.", msg.BookingId);
            return;
        }

        // Must use the same split as the credit path, or a full refund would reverse a different
        // amount than was credited and leave a centavo stranded in the wallet. See EarningsSplit.
        var driverAmount = EarningsSplit.For(msg.AmountRefunded, _options.PlatformCommissionRate).DriverAmount;
        if (driverAmount <= 0)
            return;

        try
        {
            wallet.SubtractForRefund(driverAmount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to subtract refund amount {Amount} from driver {DriverId} wallet for booking {BookingId}.",
                driverAmount, msg.DriverId, msg.BookingId);
            throw;
        }

        var transaction = new WalletTransaction(
            wallet.Id,
            WalletTransactionType.EarningReversal,
            WalletBucket.Personal,
            driverAmount,
            WalletTransactionStatus.Completed,
            reversalDescription,
            msg.BookingId);

        // Row and balance committed together - see ApplyTransactionAsync.
        await _walletRepository.ApplyTransactionAsync(wallet, transaction, ct);

        _logger.LogInformation(
            "Reversed {Amount} from driver {DriverId} Personal wallet for refund (booking {BookingId})",
            driverAmount, msg.DriverId, msg.BookingId);
    }
}
