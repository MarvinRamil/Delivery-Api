using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Bookings.Infrastructure.Repositories;

public class CustomerRepository : Repository<Customer, BookingsDbContext>, ICustomerRepository
{
    public CustomerRepository(BookingsDbContext context) : base(context) { }

    public async Task<Customer?> GetByEmailAsync(string email, CancellationToken ct = default)
        => await DbSet.FirstOrDefaultAsync(c => c.Email == email, ct);

    public async Task<IReadOnlyList<Customer>> GetActiveCustomersAsync(CancellationToken ct = default)
        => await DbSet.Where(c => c.IsActive).ToListAsync(ct);

    // Override GetAllAsync to ignore query filters for admin views (include soft-deleted customers)
    public override async Task<IReadOnlyList<Customer>> GetAllAsync(CancellationToken ct = default)
        => await DbSet.IgnoreQueryFilters().ToListAsync(ct);

    /// <inheritdoc />
    public async Task<(IReadOnlyList<Customer> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var query = DbSet.IgnoreQueryFilters();
        var totalCount = await query.CountAsync(ct);
        var skip = (page - 1) * pageSize;
        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, totalCount);
    }
}
