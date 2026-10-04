using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;

namespace BeeLogistics.Shared.Hubs;

[Authorize]
public class NotificationHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        if (!string.IsNullOrEmpty(userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.UserIdentifier;
        if (!string.IsNullOrEmpty(userId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user-{userId}");
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Join a group by name. For finance-related groups (e.g. "driver-{driverId}") only the owning driver or backoffice can join.
    /// </summary>
    public async Task JoinGroup(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            return;

        // Restrict "driver-{guid}" to the owning driver or backoffice (finance: prevent cross-driver data leak)
        const string driverPrefix = "driver-";
        if (groupName.StartsWith(driverPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var driverIdStr = groupName.Substring(driverPrefix.Length).Trim();
            if (!Guid.TryParse(driverIdStr, out var driverId))
                return;

            var userId = Context.UserIdentifier;
            var isBackoffice = Context.User?.HasClaim(c => c.Type == "is_backoffice" && c.Value == "true") == true
                && (Context.User?.IsInRole("SuperAdmin") == true || Context.User?.IsInRole("Admin") == true);

            var isOwner = !string.IsNullOrEmpty(userId) && Guid.TryParse(userId, out var userGuid) && userGuid == driverId;
            if (!isOwner && !isBackoffice)
                return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
    }

    public async Task LeaveGroup(string groupName)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
    }
}

// Service to send notifications from anywhere in the app
public interface INotificationService
{
    Task SendToUserAsync(string userId, string method, object data);
    Task SendToGroupAsync(string groupName, string method, object data);
    Task SendToAllAsync(string method, object data);
}

public class SignalRNotificationService : INotificationService
{
    private readonly IHubContext<NotificationHub> _hubContext;

    public SignalRNotificationService(IHubContext<NotificationHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task SendToUserAsync(string userId, string method, object data)
    {
        await _hubContext.Clients.Group($"user-{userId}").SendAsync(method, data);
    }

    public async Task SendToGroupAsync(string groupName, string method, object data)
    {
        await _hubContext.Clients.Group(groupName).SendAsync(method, data);
    }

    public async Task SendToAllAsync(string method, object data)
    {
        await _hubContext.Clients.All.SendAsync(method, data);
    }
}
