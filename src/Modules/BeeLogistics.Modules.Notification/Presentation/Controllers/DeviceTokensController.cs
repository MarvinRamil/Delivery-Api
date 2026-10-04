using BeeLogistics.Modules.Notification.Application.Commands;
using BeeLogistics.Modules.Notification.Application.DTOs;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BeeLogistics.Modules.Notification.Presentation.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public class DeviceTokensController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IPushProviderPolicy _pushProviderPolicy;
    private readonly IDirectBusPublisher _directBusPublisher;
    private readonly IDeviceTokenRepository _deviceTokenRepository;
    private readonly IPushDispatcher _pushDispatcher;

    public DeviceTokensController(
        IMediator mediator,
        IPushProviderPolicy pushProviderPolicy,
        IDirectBusPublisher directBusPublisher,
        IDeviceTokenRepository deviceTokenRepository,
        IPushDispatcher pushDispatcher)
    {
        _mediator = mediator;
        _pushProviderPolicy = pushProviderPolicy;
        _directBusPublisher = directBusPublisher;
        _deviceTokenRepository = deviceTokenRepository;
        _pushDispatcher = pushDispatcher;
    }

    /// <summary>
    /// Returns the active push vendor and the token type the backend expects per
    /// platform. Clients call this to detect a vendor change and re-register with the
    /// correct token type.
    /// </summary>
    [HttpGet("push-config")]
    public IActionResult GetPushConfig()
    {
        return Ok(new
        {
            provider = _pushProviderPolicy.ActiveProvider,
            expected = new
            {
                ios = _pushProviderPolicy.ExpectedTokenType("ios"),
                android = _pushProviderPolicy.ExpectedTokenType("android")
            }
        });
    }

    /// <summary>
    /// Send a push notification. Backoffice only.
    /// Provide one target: deviceToken, deviceTokens, userId+appType, userIds+appType, or appType+burst=true.
    /// Async: the push is queued onto the SendPush bus and delivered by SendPushConsumer.
    /// Returns 202 with the record id; check the send outcome via GET api/notifications/history.
    /// </summary>
    [HttpPost("send")]
    [Authorize(Policy = "Backoffice")]
    public async Task<IActionResult> SendPush([FromBody] SendPushRequestDto request, CancellationToken ct)
    {
        if (request == null)
            return BadRequest(new { message = "Request body is required." });

        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Body))
            return BadRequest(new { message = "Title and Body are required." });

        var recordId = await _pushDispatcher.DispatchAsync(request.ToSendPushRequested(), "backoffice", ct);

        return Accepted(new { recordId, message = "Push queued for delivery." });
    }

    /// <summary>
    /// Unified send across email / SMS / push — all together or individually. Backoffice only.
    /// Set "channels" to any of "email", "sms", "push" and populate the matching sub-object.
    /// Publishes to MassTransit for async, decoupled delivery — returns 202 Accepted
    /// (delivery happens in the SendNotificationConsumer; per-channel results are logged).
    /// </summary>
    [HttpPost("dispatch")]
    [Authorize(Policy = "Backoffice")]
    public async Task<IActionResult> Dispatch([FromBody] SendNotificationRequestDto request, CancellationToken ct)
    {
        Console.WriteLine($"[NOTIF-TRACE] 1. Dispatch endpoint HIT. channels={string.Join(",", request?.Channels ?? new())}");

        if (request == null)
            return BadRequest(new { message = "Request body is required." });

        var channels = (request.Channels ?? new List<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToLowerInvariant())
            .ToList();
        if (channels.Count == 0)
            return BadRequest(new { message = "At least one channel is required: email, sms, or push." });

        var message = new SendNotificationRequested
        {
            Channels = channels,
            Email = request.Email is null
                ? null
                : new EmailChannelPayload(
                    request.Email.To, request.Email.Subject, request.Email.Body, request.Email.IsHtml,
                    request.Email.Cc, request.Email.Bcc, request.Email.TemplateName, request.Email.Placeholders),
            Sms = request.Sms is null ? null : new SmsChannelPayload(request.Sms.To, request.Sms.Message),
            Push = request.Push is null
                ? null
                : new SendPushRequested
                {
                    Title = request.Push.Title,
                    Body = request.Push.Body,
                    Data = request.Push.Data,
                    DeviceToken = request.Push.DeviceToken,
                    DeviceTokens = request.Push.DeviceTokens,
                    UserId = request.Push.UserId,
                    UserIds = request.Push.UserIds,
                    AppType = request.Push.AppType,
                    Burst = request.Push.Burst,
                },
        };

        Console.WriteLine($"[NOTIF-TRACE] 2. Dispatch about to Publish SendNotificationRequested to RabbitMQ...");
        await _directBusPublisher.PublishAsync(message, ct);
        Console.WriteLine($"[NOTIF-TRACE] 3. Dispatch published OK -> returning 202. (If you DON'T see step 4, the consumer isn't running / RabbitMQ down.)");

        return Accepted(new { message = "Notification queued for delivery.", channels });
    }

    /// <summary>
    /// List the active registered devices for a user, so the backoffice can target a
    /// specific device when sending a push. Backoffice only.
    /// </summary>
    [HttpGet("device-tokens")]
    [Authorize(Policy = "Backoffice")]
    public async Task<IActionResult> GetDeviceTokens(
        [FromQuery] string userId,
        [FromQuery] string appType,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest(new { message = "userId is required." });

        appType = (appType ?? string.Empty).Trim().ToLowerInvariant();
        if (appType != "customer" && appType != "driver")
            return BadRequest(new { message = "appType must be 'customer' or 'driver'." });

        var tokens = await _deviceTokenRepository.GetByUserIdAsync(userId, appType, ct);

        var data = tokens
            .OrderByDescending(t => t.LastUsedAt)
            .Select(t => new
            {
                id = t.Id,
                token = t.Token,
                platform = t.Platform,
                appType = t.AppType,
                tokenType = t.TokenType,
                lastUsedAt = t.LastUsedAt
            });

        return Ok(new { success = true, data });
    }

    /// <summary>
    /// Register a device token for push notifications
    /// </summary>
    [HttpPost("register-device")]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceTokenDto dto, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        // Validate platform
        if (dto.Platform != "ios" && dto.Platform != "android")
        {
            return BadRequest(new { message = "Platform must be 'ios' or 'android'" });
        }

        // Validate app type
        if (dto.AppType != "customer" && dto.AppType != "driver")
        {
            return BadRequest(new { message = "AppType must be 'customer' or 'driver'" });
        }

        // Validate token type flag when provided (drives push routing).
        if (!string.IsNullOrWhiteSpace(dto.TokenType) &&
            dto.TokenType is not (PushTokenTypes.Fcm or PushTokenTypes.Apns or PushTokenTypes.Expo))
        {
            return BadRequest(new { message = "TokenType must be 'fcm', 'apns', or 'expo'" });
        }

        var command = new RegisterDeviceTokenCommand(userId, dto);
        var result = await _mediator.Send(command, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return BadRequest(new { message = result.Error });
    }

    /// <summary>
    /// Unregister a device token
    /// SECURITY: Device token must be in request body, not query parameter, to prevent logging in URL
    /// </summary>
    [HttpDelete("unregister-device")]
    public async Task<IActionResult> UnregisterDevice([FromBody] UnregisterDeviceTokenDto? dto, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        // SECURITY: Reject if device token is provided in query string
        if (Request.Query.ContainsKey("deviceToken"))
        {
            return BadRequest(new { 
                message = "Device token must be provided in request body, not query parameter." 
            });
        }

        // If device token is provided in body, unregister it
        // Otherwise, tokens are automatically removed when they become invalid
        if (dto != null && !string.IsNullOrWhiteSpace(dto.DeviceToken))
        {
            // TODO: Implement device token unregistration if needed
            // For now, tokens are automatically removed when they become invalid
        }

        return Ok(new { message = "Device token will be removed automatically if invalid" });
    }
}

public record UnregisterDeviceTokenDto(string? DeviceToken = null);

