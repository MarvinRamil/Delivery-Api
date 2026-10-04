namespace BeeLogistics.Modules.Map.Application.Interfaces;

public interface IMqttLocationService
{
    Task PublishDriverLocationAsync(Guid driverId, decimal latitude, decimal longitude, decimal? speed = null, decimal? heading = null, CancellationToken ct = default);
    Task SubscribeToDriverLocationsAsync(Func<Guid, decimal, decimal, decimal?, decimal?, Task> onLocationUpdate, CancellationToken ct = default);
    Task<bool> IsConnectedAsync();
}
