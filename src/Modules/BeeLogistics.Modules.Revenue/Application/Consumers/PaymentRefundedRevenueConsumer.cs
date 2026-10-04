using BeeLogistics.Modules.Revenue.Application.Interfaces;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Revenue.Application.Consumers;

/// <summary>
/// Consumes PaymentRefundedEvent and flips the booking's PlatformCommission record to Reversed,
/// so a refunded booking stops showing its original commission as if it still stood (e.g. in
/// GetDriverEarningsHistoryQueryHandler's verification view). Idempotent: MarkReversed() is a
/// no-op if the record is already reversed, and this consumer is a no-op if no commission was
/// ever recorded for the booking.
/// </summary>
public class PaymentRefundedRevenueConsumer : IConsumer<PaymentRefundedEvent>
{
    private readonly IPlatformCommissionRepository _repository;
    private readonly ILogger<PaymentRefundedRevenueConsumer> _logger;

    public PaymentRefundedRevenueConsumer(
        IPlatformCommissionRepository repository,
        ILogger<PaymentRefundedRevenueConsumer> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PaymentRefundedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var commission = await _repository.GetByBookingIdAsync(msg.BookingId, ct);
        if (commission == null)
        {
            _logger.LogWarning(
                "[Revenue] PaymentRefundedEvent for BookingId {BookingId} has no matching PlatformCommission - nothing to reverse.",
                msg.BookingId);
            return;
        }

        commission.MarkReversed();
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("[Revenue] Marked PlatformCommission reversed - BookingId: {BookingId}", msg.BookingId);
    }
}
