using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Domain;
using BeeLogistics.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Notification.Infrastructure.Services;

/// <summary>
/// The single choke point for push. Persists a Queued <see cref="PushNotificationRecord"/>
/// and publishes <see cref="SendPushRequested"/> (carrying the record Id) onto the SendPush
/// queue. SendPushConsumer then delivers it and flips the record to Sent/Failed. Mirrors the
/// EmailQueueService (write Queued) + EmailSendingJob (update outcome) split.
/// </summary>
public class PushDispatchService : IPushDispatcher
{
    private readonly IPushRecordRepository _records;
    private readonly IDirectBusPublisher _directBusPublisher;
    private readonly ILogger<PushDispatchService> _logger;

    public PushDispatchService(
        IPushRecordRepository records,
        IDirectBusPublisher directBusPublisher,
        ILogger<PushDispatchService> logger)
    {
        _records = records;
        _directBusPublisher = directBusPublisher;
        _logger = logger;
    }

    public async Task<Guid> DispatchAsync(SendPushRequested request, string? source = null, CancellationToken ct = default)
    {
        var (mode, targetValue) = DescribeTarget(request);

        var record = new PushNotificationRecord
        {
            Id = Guid.NewGuid(),
            Title = Truncate(request.Title, 200),
            Body = Truncate(request.Body, 1000),
            TargetMode = mode,
            TargetValue = targetValue is null ? null : Truncate(targetValue, 500),
            AppType = request.AppType,
            Status = PushStatus.Queued,
            Source = source,
            CreatedAt = DateTime.UtcNow
        };

        await _records.AddAsync(record, ct);

        await _directBusPublisher.PublishAsync(request with { RecordId = record.Id }, ct);

        _logger.LogInformation(
            "Push queued (RecordId {RecordId}, target {Mode}, source {Source}): {Title}",
            record.Id, mode, source ?? "unknown", request.Title);

        return record.Id;
    }

    private static (string Mode, string? TargetValue) DescribeTarget(SendPushRequested r)
    {
        if (!string.IsNullOrWhiteSpace(r.DeviceToken))
            return ("device", r.DeviceToken);

        if (r.DeviceTokens is { Count: > 0 })
            return ("devices", $"{r.DeviceTokens.Count} device(s)");

        if (r.Burst && !string.IsNullOrWhiteSpace(r.AppType))
            return ("broadcast", null);

        if (r.UserIds is { Count: > 0 })
            return ("users", $"{r.UserIds.Count} user(s)");

        if (!string.IsNullOrWhiteSpace(r.UserId))
            return ("user", r.UserId);

        return ("unknown", null);
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
