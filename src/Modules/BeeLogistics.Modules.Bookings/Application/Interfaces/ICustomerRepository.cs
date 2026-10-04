using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Bookings.Application.Interfaces;

public interface ICustomerRepository : IRepository<Customer>
{
    Task<Customer?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<IReadOnlyList<Customer>> GetActiveCustomersAsync(CancellationToken ct = default);
    /// <summary>
    /// Gets customers with server-side pagination (includes soft-deleted for admin view).
    /// </summary>
    Task<(IReadOnlyList<Customer> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken ct = default);
}
