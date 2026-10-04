using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Notification.Infrastructure.Repositories;

public class DeviceTokenRepository : IDeviceTokenRepository
{
    private readonly NotificationDbContext _context;

    public DeviceTokenRepository(NotificationDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Finds a device token regardless of whether it is active.
    ///
    /// <para>
    /// The <c>IsActive</c> filter is deliberately absent, because the unique index is on
    /// <c>Token</c> alone. Filtering here made a <b>deactivated</b> token invisible to registration:
    /// the lookup returned null, the handler took the create path, and the INSERT collided on
    /// <c>IX_DeviceTokens_Token</c> — every time, not intermittently. A device whose token was ever
    /// deactivated could never register again.
    /// </para>
    /// <para>
    /// A lookup that guards an insert has to match the constraint it is guarding, or it is not a
    /// guard. Callers that only want live tokens should filter after, or use
    /// <see cref="GetByTokensAsync"/>, which is for sending and correctly excludes inactive rows.
    /// </para>
    /// </summary>
    public async Task<DeviceToken?> GetByTokenAsync(string token, CancellationToken ct = default)
    {
        return await _context.DeviceTokens
            .FirstOrDefaultAsync(dt => dt.Token == token, ct);
    }

    public async Task<IReadOnlyList<DeviceToken>> GetByTokensAsync(IEnumerable<string> tokens, CancellationToken ct = default)
    {
        var tokenList = tokens.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        if (tokenList.Count == 0)
            return Array.Empty<DeviceToken>();

        return await _context.DeviceTokens
            .Where(dt => tokenList.Contains(dt.Token) && dt.IsActive)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DeviceToken>> GetByUserIdAsync(string userId, string appType, CancellationToken ct = default)
    {
        return await _context.DeviceTokens
            .Where(dt => dt.UserId == userId && dt.AppType == appType && dt.IsActive)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DeviceToken>> GetByUserIdAndPlatformAsync(string userId, string platform, string appType, CancellationToken ct = default)
    {
        return await _context.DeviceTokens
            .Where(dt => dt.UserId == userId && dt.Platform == platform && dt.AppType == appType && dt.IsActive)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DeviceToken>> GetAllByAppTypeAsync(string appType, CancellationToken ct = default)
    {
        return await _context.DeviceTokens
            .Where(dt => dt.AppType == appType && dt.IsActive)
            .ToListAsync(ct);
    }

    public async Task AddAsync(DeviceToken deviceToken, CancellationToken ct = default)
    {
        await _context.DeviceTokens.AddAsync(deviceToken, ct);
    }

    public async Task UpdateAsync(DeviceToken deviceToken, CancellationToken ct = default)
    {
        _context.DeviceTokens.Update(deviceToken);
        await Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var token = await _context.DeviceTokens.FindAsync(new object[] { id }, ct);
        if (token != null)
        {
            _context.DeviceTokens.Remove(token);
        }
    }

    public async Task DeleteByTokenAsync(string token, CancellationToken ct = default)
    {
        var deviceToken = await GetByTokenAsync(token, ct);
        if (deviceToken != null)
        {
            _context.DeviceTokens.Remove(deviceToken);
        }
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }
}

