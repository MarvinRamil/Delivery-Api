namespace BeeLogistics.Modules.Bookings.Application.Interfaces;

/// <summary>
/// Serializes one driver's accepts so two simultaneous ones cannot both pass the concurrency cap.
/// </summary>
/// <remarks>
/// The cap is read-then-write: count the driver's held bookings, then save one more. Two accepts
/// running at once would both read the same count and both pass, leaving the driver one over the
/// limit. The booking's concurrency token does not help here — the two accepts touch two
/// different bookings, so neither save conflicts.
///
/// Held from before the count until after the save. Not committing (an exception, or a rejected
/// accept) discards the work.
/// </remarks>
public interface IDriverCapacityScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
}
