using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Drivers.Application.Consumers;

public class CashDeliverySettlementConsumer : IConsumer<BookingCompletedEvent>
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly IMediator _mediator;
    private readonly DriverWalletOptions _options;
    private readonly ILogger<CashDeliverySettlementConsumer> _logger;

    public CashDeliverySettlementConsumer(
        IPaymentRepository paymentRepository,
        IMediator mediator,
        IOptions<DriverWalletOptions> options,
        ILogger<CashDeliverySettlementConsumer> logger)
    {
        _paymentRepository = paymentRepository;
        _mediator = mediator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<BookingCompletedEvent> context)
    {
        var msg = context.Message;
        var payment = await _paymentRepository.GetByBookingIdAsync(msg.BookingId, context.CancellationToken);
        if (payment == null || payment.Method != PaymentMethod.Cash)
            return;

        var charge = decimal.Round(payment.Amount * _options.CashDeliveryPlatformChargeRate, 2, MidpointRounding.AwayFromZero);
        if (charge <= 0)
            return;

        var result = await _mediator.Send(
            new ApplyCashSettlementDebitCommand(msg.DriverId, msg.BookingId, charge),
            context.CancellationToken);

        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "Cash settlement debit failed for booking {BookingId}, driver {DriverId}. Reason: {Reason}",
                msg.BookingId,
                msg.DriverId,
                result.Error);

            // Rethrow so this re-enters MassTransit's retry/redelivery pipeline instead of being
            // silently dropped. Safe because the handler is now booking-id idempotent and atomic
            // (see ApplyCashSettlementDebitCommandHandler): a retried delivery either completes
            // the debit or short-circuits on the already-applied check, never double-debits.
            throw new InvalidOperationException(
                $"Cash settlement debit failed for booking {msg.BookingId}, driver {msg.DriverId}: {result.Error}");
        }
    }
}
