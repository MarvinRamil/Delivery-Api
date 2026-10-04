using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Payment.Infrastructure.Repositories;

public class SavedPaymentMethodRepository : Repository<SavedPaymentMethod, PaymentDbContext>, ISavedPaymentMethodRepository
{
    public SavedPaymentMethodRepository(PaymentDbContext context) : base(context) { }

    public async Task<IReadOnlyList<SavedPaymentMethod>> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default)
    {
        return await DbSet
            .Where(pm => pm.CustomerId == customerId)
            .OrderByDescending(pm => pm.IsDefault)
            .ThenByDescending(pm => pm.LastUsedAt)
            .ThenByDescending(pm => pm.CreatedAt)
            .ToListAsync(ct);
    }

    public new async Task<SavedPaymentMethod?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await DbSet.FirstOrDefaultAsync(pm => pm.Id == id, ct);
    }

    public async Task<SavedPaymentMethod?> GetDefaultAsync(Guid customerId, CancellationToken ct = default)
    {
        return await DbSet
            .FirstOrDefaultAsync(pm => pm.CustomerId == customerId && pm.IsDefault, ct);
    }

    public async Task<SavedPaymentMethod?> GetByProviderPaymentMethodIdAsync(string provider, string providerPaymentMethodId, CancellationToken ct = default)
    {
        return await DbSet
            .FirstOrDefaultAsync(pm => pm.Provider == provider && pm.ProviderPaymentMethodId == providerPaymentMethodId, ct);
    }

    public async Task UnsetAllDefaultsAsync(Guid customerId, CancellationToken ct = default)
    {
        var defaults = await DbSet
            .Where(pm => pm.CustomerId == customerId && pm.IsDefault)
            .ToListAsync(ct);
        
        foreach (var pm in defaults)
        {
            pm.UnsetAsDefault();
        }
        
        // Note: SaveChangesAsync should be called by the caller after this method
    }
}
