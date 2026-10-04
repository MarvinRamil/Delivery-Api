using BeeLogistics.Modules.Messaging.Domain;

namespace BeeLogistics.Modules.Messaging.Application.Interfaces;

public interface IMatrixIdentityRepository
{
    Task<MatrixIdentity?> GetByBeeUserIdAsync(string beeUserId, CancellationToken ct = default);
    Task<MatrixIdentity?> GetByMatrixUserIdAsync(string matrixUserId, CancellationToken ct = default);

    /// <summary>
    /// Adds a mapping, or returns the existing one if another caller won the race.
    /// </summary>
    /// <remarks>
    /// Two consumers can provision the same customer concurrently — a booking and a driver
    /// assignment arriving together. The unique index makes that a database error rather than a
    /// duplicate, and this method turns that error back into the answer the caller wanted.
    /// </remarks>
    Task<MatrixIdentity> AddOrGetAsync(MatrixIdentity identity, CancellationToken ct = default);
}
