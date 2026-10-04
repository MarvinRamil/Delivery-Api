using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Authorization;
using BeeLogistics.Shared.Contracts;

namespace BeeLogistics.Modules.Map.Presentation.Hubs;

[Authorize]
public class LocationHub : Hub
{
    // Deliberately generic: the caller must not learn whether the driver exists or why the
    // subscription was refused.
    private const string NotAuthorizedMessage = "Not authorized to track this driver.";

    private readonly ILogger<LocationHub> _logger;
    private readonly ILiveTrackingAuthorizer _trackingAuthorizer;

    public LocationHub(ILogger<LocationHub> logger, ILiveTrackingAuthorizer trackingAuthorizer)
    {
        _logger = logger;
        _trackingAuthorizer = trackingAuthorizer;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        var userRole = Context.User?.FindFirst("role")?.Value ?? Context.User?.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;

        // Clients receive location updates through per-driver groups, joined on demand
        // via JoinDriverGroup. (The tenant-scoped group went away with #43.)
        _logger.LogInformation("Client {ConnectionId} (User: {UserId}, Role: {Role}) connected - will join driver groups as needed",
            Context.ConnectionId, userId, userRole);

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Join a driver-specific group to receive location updates for a specific driver
    /// Used by customers tracking their booking's driver
    /// </summary>
    public async Task JoinDriverGroup(string driverId)
    {
        if (string.IsNullOrEmpty(driverId))
        {
            _logger.LogWarning("Client {ConnectionId} attempted to join driver group with empty driverId", Context.ConnectionId);
            throw new HubException(NotAuthorizedMessage);
        }

        // Driver group names are always canonical GUIDs (matching broadcasts). Reject anything else.
        if (!Guid.TryParse(driverId.Trim(), out var guidDriverId))
        {
            _logger.LogWarning("Client {ConnectionId} attempted to join driver group with non-GUID driverId {DriverId}", Context.ConnectionId, driverId);
            throw new HubException(NotAuthorizedMessage);
        }

        // AUTHORIZATION: prevent cross-driver location tracking (IDOR). Only the driver
        // themselves, backoffice, or a customer with an active booking with this driver
        // may subscribe to the live location stream.
        if (!await CanJoinDriverGroupAsync(guidDriverId))
        {
            _logger.LogWarning("Client {ConnectionId} (User {UserId}) denied JoinDriverGroup for driver {DriverId}",
                Context.ConnectionId, Context.UserIdentifier, guidDriverId);
            // Surfaces to the caller as a rejected invocation. Returning quietly would leave the
            // client believing it is subscribed while no location ever arrives.
            throw new HubException(NotAuthorizedMessage);
        }

        var groupName = $"driver-{guidDriverId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} joined driver group {GroupName}", Context.ConnectionId, groupName);
    }

    private async Task<bool> CanJoinDriverGroupAsync(Guid driverId)
    {
        var userId = Context.UserIdentifier;
        var isSelf = Guid.TryParse(userId, out var userGuid) && userGuid == driverId;
        if (isSelf)
            return true;

        var isBackoffice = Context.User?.HasClaim(c => c.Type == "is_backoffice" && c.Value == "true") == true
            && (Context.User.IsInRole("SuperAdmin") || Context.User.IsInRole("Admin"));
        if (isBackoffice)
            return true;

        if (Guid.TryParse(userId, out userGuid))
            return await _trackingAuthorizer.CanTrackDriverAsync(userGuid, driverId, Context.ConnectionAborted);

        return false;
    }

    /// <summary>
    /// Leave a driver-specific group
    /// </summary>
    public async Task LeaveDriverGroup(string driverId)
    {
        if (string.IsNullOrEmpty(driverId) || !Guid.TryParse(driverId.Trim(), out var guidDriverId))
        {
            return;
        }

        // Must match the canonical form used when joining, or the connection stays subscribed.
        var groupName = $"driver-{guidDriverId}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} left driver group {GroupName}", Context.ConnectionId, groupName);
    }
}
