using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Messaging.Infrastructure.Repositories;

public sealed class MatrixIdentityRepository : IMatrixIdentityRepository
{
    private readonly MessagingDbContext _db;

    public MatrixIdentityRepository(MessagingDbContext db) => _db = db;

    public Task<MatrixIdentity?> GetByBeeUserIdAsync(string beeUserId, CancellationToken ct = default) =>
        _db.MatrixIdentities.FirstOrDefaultAsync(x => x.BeeUserId == beeUserId, ct);

    public Task<MatrixIdentity?> GetByMatrixUserIdAsync(string matrixUserId, CancellationToken ct = default) =>
        _db.MatrixIdentities.FirstOrDefaultAsync(x => x.MatrixUserId == matrixUserId, ct);

    public async Task<MatrixIdentity> AddOrGetAsync(MatrixIdentity identity, CancellationToken ct = default)
    {
        _db.MatrixIdentities.Add(identity);
        try
        {
            await _db.SaveChangesAsync(ct);
            return identity;
        }
        catch (DbUpdateException)
        {
            // Lost a race against concurrent provisioning — a booking and a driver assignment for
            // the same person can arrive together. Detach ours so the context is not left holding
            // a rejected entity, then take the row the winner wrote.
            _db.Entry(identity).State = EntityState.Detached;

            var existing = await GetByBeeUserIdAsync(identity.BeeUserId, ct);
            if (existing is not null) return existing;

            // Not a race after all: something else rejected the insert and swallowing it would
            // hide a real fault.
            throw;
        }
    }
}
