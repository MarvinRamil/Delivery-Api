namespace BeeLogistics.Modules.Notification.Application.Interfaces;

public interface IDeviceTokenRepository
{
    Task<Domain.DeviceToken?> GetByTokenAsync(string token, CancellationToken ct = default);
    Task<IReadOnlyList<Domain.DeviceToken>> GetByTokensAsync(IEnumerable<string> tokens, CancellationToken ct = default);
    Task<IReadOnlyList<Domain.DeviceToken>> GetByUserIdAsync(string userId, string appType, CancellationToken ct = default);
    Task<IReadOnlyList<Domain.DeviceToken>> GetAllByAppTypeAsync(string appType, CancellationToken ct = default);
    Task<IReadOnlyList<Domain.DeviceToken>> GetByUserIdAndPlatformAsync(string userId, string platform, string appType, CancellationToken ct = default);
    Task AddAsync(Domain.DeviceToken deviceToken, CancellationToken ct = default);
    Task UpdateAsync(Domain.DeviceToken deviceToken, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task DeleteByTokenAsync(string token, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

