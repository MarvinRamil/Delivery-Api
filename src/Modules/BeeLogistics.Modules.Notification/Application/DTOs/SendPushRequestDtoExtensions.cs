using BeeLogistics.Shared.Contracts;

namespace BeeLogistics.Modules.Notification.Application.DTOs;

public static class SendPushRequestDtoExtensions
{
    /// <summary>
    /// Maps the HTTP/DTO push shape onto the bus contract published to the SendPush queue.
    /// RecordId is left empty here; the dispatcher stamps it after creating the record.
    /// </summary>
    public static SendPushRequested ToSendPushRequested(this SendPushRequestDto dto) => new()
    {
        Title = dto.Title,
        Body = dto.Body,
        Data = dto.Data,
        DeviceToken = dto.DeviceToken,
        DeviceTokens = dto.DeviceTokens,
        UserId = dto.UserId,
        UserIds = dto.UserIds,
        AppType = dto.AppType,
        Burst = dto.Burst
    };
}
