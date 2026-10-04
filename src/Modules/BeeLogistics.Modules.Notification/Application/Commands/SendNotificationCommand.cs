using BeeLogistics.Modules.Notification.Application.DTOs;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Application.Services;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Notification.Application.Commands;

/// <summary>
/// Unified send across email / SMS / push. Fans out to the existing per-channel
/// services; each channel is isolated so a failure in one is reported but does not
/// abort the others. Returns Result.Ok with per-channel results (partial success);
/// only an invalid request (no channels) fails outright.
/// </summary>
public record SendNotificationCommand(SendNotificationRequestDto Request) : IRequest<Result<SendNotificationResponseDto>>;

public class SendNotificationCommandHandler : IRequestHandler<SendNotificationCommand, Result<SendNotificationResponseDto>>
{
    private readonly IEmailService _emailService;
    private readonly IEmailTemplateService _emailTemplateService;
    private readonly ISmsService _smsService;
    private readonly IPushDispatcher _pushDispatcher;
    private readonly ILogger<SendNotificationCommandHandler> _logger;

    public SendNotificationCommandHandler(
        IEmailService emailService,
        IEmailTemplateService emailTemplateService,
        ISmsService smsService,
        IPushDispatcher pushDispatcher,
        ILogger<SendNotificationCommandHandler> logger)
    {
        _emailService = emailService;
        _emailTemplateService = emailTemplateService;
        _smsService = smsService;
        _pushDispatcher = pushDispatcher;
        _logger = logger;
    }

    public async Task<Result<SendNotificationResponseDto>> Handle(SendNotificationCommand request, CancellationToken ct)
    {
        var r = request.Request;

        Console.WriteLine($"[NOTIF-TRACE] 6. SendNotificationCommandHandler.Handle ENTERED. channels={string.Join(",", r.Channels ?? new())}");

        var channels = (r.Channels ?? new List<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToLowerInvariant())
            .ToHashSet();

        if (channels.Count == 0)
            return Result.Fail<SendNotificationResponseDto>("At least one channel is required: email, sms, or push.");

        EmailChannelResult? emailResult = null;
        SmsChannelResult? smsResult = null;
        PushChannelResult? pushResult = null;
        var errors = new List<string>();

        // EMAIL (queued via Hangfire)
        if (channels.Contains("email"))
        {
            if (r.Email == null)
            {
                errors.Add("email channel requested but 'email' payload is missing.");
            }
            else
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(r.Email.TemplateName))
                    {
                        // Named template path (legacy {{key}} replacement on the raw body).
                        var message = new EmailMessage(
                            r.Email.To, r.Email.Subject, r.Email.Body, r.Email.IsHtml,
                            From: null, FromName: null, Cc: r.Email.Cc, Bcc: r.Email.Bcc);
                        await _emailService.SendTemplatedAsync(r.Email.TemplateName, message, r.Email.Placeholders ?? new(), ct);
                    }
                    else
                    {
                        // Free-form email -> wrap in the standard branded template so it matches
                        // the app's other emails (logo header, footer, styling).
                        var messageHtml = r.Email.IsHtml
                            ? r.Email.Body
                            : System.Net.WebUtility.HtmlEncode(r.Email.Body).Replace("\n", "<br>");
                        var branded = _emailTemplateService
                            .CreateCustomNotificationEmail(r.Email.To, r.Email.Subject, messageHtml)
                            with { Cc = r.Email.Cc, Bcc = r.Email.Bcc };
                        await _emailService.SendAsync(branded, ct);
                    }

                    emailResult = new EmailChannelResult(true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unified send: email channel failed for {To}", r.Email.To);
                    emailResult = new EmailChannelResult(false);
                    errors.Add($"email: {ex.Message}");
                }
            }
        }

        // SMS (synchronous)
        if (channels.Contains("sms"))
        {
            if (r.Sms == null)
            {
                errors.Add("sms channel requested but 'sms' payload is missing.");
            }
            else
            {
                try
                {
                    var sent = await _smsService.SendAsync(r.Sms.To, r.Sms.Message, ct);
                    smsResult = new SmsChannelResult(sent, sent ? null : "provider did not accept the message");
                    if (!sent) errors.Add("sms: provider did not accept the message");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unified send: sms channel failed for {To}", r.Sms.To);
                    smsResult = new SmsChannelResult(false, ex.Message);
                    errors.Add($"sms: {ex.Message}");
                }
            }
        }

        // PUSH (queued onto the SendPush bus via the dispatcher — routing, targeting modes,
        // invalid-token pruning, and the persisted send record all happen downstream)
        if (channels.Contains("push"))
        {
            if (r.Push == null)
            {
                errors.Add("push channel requested but 'push' payload is missing.");
            }
            else
            {
                try
                {
                    Console.WriteLine($"[NOTIF-TRACE] 7. Command PUSH branch -> IPushDispatcher.DispatchAsync (writes record + publishes to SendPush)...");
                    var recordId = await _pushDispatcher.DispatchAsync(r.Push.ToSendPushRequested(), "backoffice", ct);
                    Console.WriteLine($"[NOTIF-TRACE] 7b. Push dispatched. recordId={recordId}");
                    pushResult = new PushChannelResult(true, recordId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unified send: push channel failed");
                    pushResult = new PushChannelResult(false, null, ex.Message);
                    errors.Add($"push: {ex.Message}");
                }
            }
        }

        return Result.Ok(new SendNotificationResponseDto
        {
            Email = emailResult,
            Sms = smsResult,
            Push = pushResult,
            Errors = errors
        });
    }
}
