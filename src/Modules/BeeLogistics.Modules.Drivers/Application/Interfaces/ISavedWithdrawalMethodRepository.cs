using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Application.Interfaces;

public interface ISavedWithdrawalMethodRepository : IRepository<Domain.SavedWithdrawalMethod>
{
    /// <summary>
    /// Get all saved withdrawal methods for a driver.
    /// </summary>
    Task<IReadOnlyList<Domain.SavedWithdrawalMethod>> GetByDriverIdAsync(Guid driverId, CancellationToken ct = default);
    
    /// <summary>
    /// Get a specific saved withdrawal method by ID.
    /// </summary>
    new Task<Domain.SavedWithdrawalMethod?> GetByIdAsync(Guid id, CancellationToken ct = default);
    
    /// <summary>
    /// Get the default withdrawal method for a driver.
    /// </summary>
    Task<Domain.SavedWithdrawalMethod?> GetDefaultAsync(Guid driverId, CancellationToken ct = default);
    
    /// <summary>
    /// Unset all default withdrawal methods for a driver (before setting a new default).
    /// </summary>
    Task UnsetAllDefaultsAsync(Guid driverId, CancellationToken ct = default);
}
