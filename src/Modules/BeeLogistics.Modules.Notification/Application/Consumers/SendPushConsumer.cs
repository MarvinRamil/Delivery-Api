using BeeLogistics.Modules.Notification.Application.Commands;
using BeeLogistics.Modules.Notification.Application.DTOs;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Notification.Application.Consumers;

/// <summary>
/// Drains the SendPush queue: delegates to <see cref="SendPushCommand"/> (targeting modes,
/// flag-based routing, invalid-token pruning) to actually deliver, then updates the
/// pre-created <see cref="PushNotificationRecord"/> to Sent/Failed with the device count.
/// Push is best-effort — the outcome is recorded and the message completes (no rethrow),
/// so a delivery failure does not trigger bus retries.
/// </summary>
public class SendPushConsumer : IConsumer<SendPushRequested>
{
    private readonly IMediator _mediator;
    private readonly IPushRecordRepository _records;
    private readonly ILogger<SendPushConsumer> _logger;

    public SendPushConsumer(IMediator mediator, IPushRecordRepository records, ILogger<SendPushConsumer> logger)
    {
        _mediator = mediator;
        _records = records;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SendPushRequested> context)
    {
        var m = context.Message;

        Console.WriteLine($"[NOTIF-TRACE] 9. SendPushConsumer RECEIVED from SendPush queue. title='{m.Title}', recordId={m.RecordId}");

        var dto = new SendPushRequestDto
        {
            Title = m.Title,
            Body = m.Body,
            Data = m.Data,
            DeviceToken = m.DeviceToken,
            DeviceTokens = m.DeviceTokens,
            UserId = m.UserId,
            UserIds = m.UserIds,
            AppType = m.AppType,
            Burst = m.Burst
        };

        // Load the record up front (if this message went through the dispatcher) so we can
        // record the outcome even when the send throws.
        var record = m.RecordId != Guid.Empty
            ? await _records.GetByIdAsync(m.RecordId, context.CancellationToken)
            : null;

        try
        {
            var result = await _mediator.Send(new SendPushCommand(dto), context.CancellationToken);

            if (result.IsSuccess)
            {
                _logger.LogInformation("SendPushRequested delivered to {Count} device(s).", result.Value!.DevicesSent);
                await MarkAsync(record, PushStatus.Sent, result.Value!.DevicesSent, null, context.CancellationToken);
            }
            else
            {
                _logger.LogWarning("SendPushRequested failed: {Error}", result.Error);
                await MarkAsync(record, PushStatus.Failed, 0, result.Error, context.CancellationToken);
            }
           Console.WriteLine("[NOTIF-TRACE] 10. SendPushConsumer MARKED record, devicesSent={}, error={}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendPushRequested threw while delivering (RecordId {RecordId})", m.RecordId);
            await MarkAsync(record, PushStatus.Failed, 0, ex.Message, context.CancellationToken);
        }
    }

    private async Task MarkAsync(PushNotificationRecord? record, PushStatus status, int devicesSent, string? error, CancellationToken ct)
    {
        if (record == null) return;

        record.Status = status;
        record.DevicesSent = devicesSent;
        if (status == PushStatus.Sent)
        {
            record.SentAt = DateTime.UtcNow;
        }
        else
        {
            record.ErrorMessage = error?.Length > 2000 ? error[..2000] : error;
            record.FailedAt = DateTime.UtcNow;
        }

        await _records.UpdateAsync(record, ct);
    }
}
