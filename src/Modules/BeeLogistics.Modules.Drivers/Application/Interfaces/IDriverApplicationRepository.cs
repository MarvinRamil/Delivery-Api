using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Application.Interfaces;

/// <summary>
/// Repository for DriverApplication aggregate.
/// Keeps data access in Infrastructure; Presentation uses this via Application layer.
/// </summary>
public interface IDriverApplicationRepository : IRepository<DriverApplication>
{
    Task<DriverApplication?> GetByUserIdAsync(string userId, CancellationToken ct = default);
    Task<DriverApplication?> GetByEmailAsync(string email, CancellationToken ct = default);
    /// <summary>Get application by UserId or Email (for create check and my-application fallback).</summary>
    Task<DriverApplication?> GetByUserIdOrEmailAsync(string? userId, string email, CancellationToken ct = default);
    Task<IReadOnlyList<DriverApplication>> GetAllByStatusAsync(DriverApplicationStatus? status, CancellationToken ct = default);
}
