using BeeLogistics.Modules.Notification.Application.DTOs;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Notification.Application.Commands;

public record SendPushCommand(SendPushRequestDto Request) : IRequest<Result<SendPushResponse>>;

public record SendPushResponse(int DevicesSent, string? Error = null);

public class SendPushCommandHandler : IRequestHandler<SendPushCommand, Result<SendPushResponse>>
{
    private readonly IPushNotificationService _pushService;
    private readonly IDeviceTokenRepository _deviceTokenRepository;
    private readonly ILogger<SendPushCommandHandler> _logger;

    public SendPushCommandHandler(
        IPushNotificationService pushService,
        IDeviceTokenRepository deviceTokenRepository,
        ILogger<SendPushCommandHandler> logger)
    {
        _pushService = pushService;
        _deviceTokenRepository = deviceTokenRepository;
        _logger = logger;
    }

    public async Task<Result<SendPushResponse>> Handle(SendPushCommand request, CancellationToken ct)
    {
        var r = request.Request;
        Console.WriteLine($"[NOTIF-TRACE] 7. SendPushCommandHandler.Handle ENTERED. title='{r.Title}', body='{r.Body}'");
        if (string.IsNullOrWhiteSpace(r.Title) || string.IsNullOrWhiteSpace(r.Body))
        {
            return Result.Fail<SendPushResponse>("Title and Body are required.");
        }

        object? data = r.Data;

        // Single device
        if (!string.IsNullOrWhiteSpace(r.DeviceToken))
        {
            Console.WriteLine($"[NOTIF-TRACE] 7b. SendPushCommandHandler.Handle SINGLE DEVICE branch ENTERED. deviceToken={r.DeviceToken}");
            var ok = await _pushService.SendToDeviceAsync(r.DeviceToken, r.Title, r.Body, data, ct);
            return Result.Ok(new SendPushResponse(ok ? 1 : 0));
        }

        // Multiple devices
        if (r.DeviceTokens != null && r.DeviceTokens.Any(t => !string.IsNullOrWhiteSpace(t)))
        {   Console.WriteLine($"[NOTIF-TRACE] 7a. SendPushCommandHandler.Handle MULTIPLE DEVICES branch ENTERED. deviceTokens={string.Join(",", r.DeviceTokens)}");
            var tokens = r.DeviceTokens.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            var count = await _pushService.SendToMultipleDevicesAsync(tokens, r.Title, r.Body, data, ct);
            return Result.Ok(new SendPushResponse(count));
        }

        // Single user (userId + appType)
        if (!string.IsNullOrWhiteSpace(r.UserId) && !string.IsNullOrWhiteSpace(r.AppType) && !r.Burst)
        {
            Console.WriteLine($"[NOTIF-TRACE] 7d. SendPushCommandHandler.Handle SINGLE USER branch ENTERED. userId={r.UserId}, appType={r.AppType}");
            if (!ValidateAppType(r.AppType, out var appTypeError))
                return Result.Fail<SendPushResponse>(appTypeError!);

            var count = await _pushService.SendToUserAsync(r.UserId, r.AppType, r.Title, r.Body, data, ct);
            return Result.Ok(new SendPushResponse(count));
        }

        // Multiple users
        if (r.UserIds != null && r.UserIds.Any() && !string.IsNullOrWhiteSpace(r.AppType))
        {
            Console.WriteLine($"[NOTIF-TRACE] 7c. SendPushCommandHandler.Handle MULTIPLE USERS branch ENTERED. userIds={string.Join(",", r.UserIds)}");
            if (!ValidateAppType(r.AppType, out var appTypeError))
                return Result.Fail<SendPushResponse>(appTypeError!);

            var total = 0;
            foreach (var userId in r.UserIds.Where(id => !string.IsNullOrWhiteSpace(id)))
            {
                var count = await _pushService.SendToUserAsync(userId, r.AppType, r.Title, r.Body, data, ct);
                total += count;
            }
            return Result.Ok(new SendPushResponse(total));
        }

        // Burst (all users of appType)
        if (r.Burst && !string.IsNullOrWhiteSpace(r.AppType))
        {
            Console.WriteLine($"[NOTIF-TRACE] 8. SendPushCommandHandler.Handle BURST branch ENTERED. appType={r.AppType}");
            if (!ValidateAppType(r.AppType, out var appTypeError))
                return Result.Fail<SendPushResponse>(appTypeError!);

            var tokens = await _deviceTokenRepository.GetAllByAppTypeAsync(r.AppType, ct);
            var tokenList = tokens.Select(t => t.Token).ToList();
            if (!tokenList.Any())
            {
                _logger.LogInformation("Burst push: no device tokens found for appType {AppType}", r.AppType);
                return Result.Ok(new SendPushResponse(0));
            }
            var count = await _pushService.SendToMultipleDevicesAsync(tokenList, r.Title, r.Body, data, ct);
            return Result.Ok(new SendPushResponse(count));
        }

        return Result.Fail<SendPushResponse>(
            "Provide one of: deviceToken, deviceTokens, userId+appType, userIds+appType, or appType+burst=true.");
    }

    private static bool ValidateAppType(string appType, out string? error)
    {
        error = null;
        if (appType == "customer" || appType == "driver")
            return true;
        error = "AppType must be 'customer' or 'driver'.";
        return false;
    }
}
