using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Payment.Application.Interfaces;

public interface ISavedPaymentMethodRepository : IRepository<Domain.SavedPaymentMethod>
{
    /// <summary>
    /// Get all saved payment methods for a customer.
    /// </summary>
    Task<IReadOnlyList<Domain.SavedPaymentMethod>> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default);
    
    /// <summary>
    /// Get a specific saved payment method by ID.
    /// </summary>
    new Task<Domain.SavedPaymentMethod?> GetByIdAsync(Guid id, CancellationToken ct = default);
    
    /// <summary>
    /// Get the default payment method for a customer.
    /// </summary>
    Task<Domain.SavedPaymentMethod?> GetDefaultAsync(Guid customerId, CancellationToken ct = default);
    
    /// <summary>
    /// Get payment method by provider payment method ID (for uniqueness check).
    /// NOTE: the token column is encrypted non-deterministically, so this equality
    /// only matches when encryption is disabled (dev); with encryption on it acts
    /// as a best-effort duplicate check.
    /// </summary>
    Task<Domain.SavedPaymentMethod?> GetByProviderPaymentMethodIdAsync(string provider, string providerPaymentMethodId, CancellationToken ct = default);
    
    /// <summary>
    /// Unset all default payment methods for a customer (before setting a new default).
    /// </summary>
    Task UnsetAllDefaultsAsync(Guid customerId, CancellationToken ct = default);
}
