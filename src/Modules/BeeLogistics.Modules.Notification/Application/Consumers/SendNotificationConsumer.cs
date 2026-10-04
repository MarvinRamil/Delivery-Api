using BeeLogistics.Modules.Notification.Application.Commands;
using BeeLogistics.Modules.Notification.Application.DTOs;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Notification.Application.Consumers;

/// <summary>
/// Handles the unified <see cref="SendNotificationRequested"/> message (published by the
/// backoffice dispatch endpoint and other modules) and delegates to the existing
/// <see cref="SendNotificationCommand"/> so email/SMS/push fan-out logic is reused.
/// </summary>
public class SendNotificationConsumer : IConsumer<SendNotificationRequested>
{
    private readonly IMediator _mediator;
    private readonly ILogger<SendNotificationConsumer> _logger;

    public SendNotificationConsumer(IMediator mediator, ILogger<SendNotificationConsumer> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SendNotificationRequested> context)
    {
        var m = context.Message;

        Console.WriteLine($"[NOTIF-TRACE] 4. SendNotificationConsumer RECEIVED message. channels={string.Join(",", m.Channels ?? new())}");

        var dto = new SendNotificationRequestDto
        {
            Channels = m.Channels ?? new List<string>(),
            Email = m.Email is null
                ? null
                : new EmailChannelDto(
                    m.Email.To, m.Email.Subject, m.Email.Body, m.Email.IsHtml,
                    m.Email.Cc, m.Email.Bcc, m.Email.TemplateName, m.Email.Placeholders),
            Sms = m.Sms is null ? null : new SmsChannelDto(m.Sms.To, m.Sms.Message),
            Push = m.Push is null
                ? null
                : new SendPushRequestDto
                {
                    Title = m.Push.Title,
                    Body = m.Push.Body,
                    Data = m.Push.Data,
                    DeviceToken = m.Push.DeviceToken,
                    DeviceTokens = m.Push.DeviceTokens,
                    UserId = m.Push.UserId,
                    UserIds = m.Push.UserIds,
                    AppType = m.Push.AppType,
                    Burst = m.Push.Burst,
                },
        };

        Console.WriteLine($"[NOTIF-TRACE] 5. SendNotificationConsumer -> _mediator.Send(SendNotificationCommand)...");
        var result = await _mediator.Send(new SendNotificationCommand(dto), context.CancellationToken);
        Console.WriteLine($"[NOTIF-TRACE] 8. SendNotificationConsumer got result. success={result.IsSuccess}");

        if (!result.IsSuccess)
        {
            // Invalid request (e.g. no channels) — log and don't retry endlessly.
            _logger.LogWarning("SendNotificationRequested rejected: {Error}", result.Error);
            return;
        }

        var r = result.Value!;
        if (r.Errors.Count > 0)
        {
            _logger.LogWarning(
                "SendNotificationRequested completed with per-channel errors: {Errors}",
                string.Join("; ", r.Errors));
        }
        else
        {
            _logger.LogInformation(
                "SendNotificationRequested delivered. Channels: [{Channels}]",
                string.Join(", ", dto.Channels));
        }
    }
}
