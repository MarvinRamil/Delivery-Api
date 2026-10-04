using System.Linq.Expressions;
using BeeLogistics.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Shared.Infrastructure;

public class Repository<T, TContext> : IRepository<T> 
    where T : Entity 
    where TContext : DbContext
{
    protected readonly TContext Context;
    protected readonly DbSet<T> DbSet;

    public Repository(TContext context)
    {
        Context = context;
        DbSet = context.Set<T>();
    }

    public virtual async Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await DbSet.FindAsync(new object[] { id }, ct);

    public virtual async Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default)
        => await DbSet.ToListAsync(ct);

    public virtual async Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => await DbSet.Where(predicate).ToListAsync(ct);

    public virtual async Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => await DbSet.FirstOrDefaultAsync(predicate, ct);

    public virtual async Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => await DbSet.AnyAsync(predicate, ct);

    public virtual async Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default)
        => predicate == null ? await DbSet.CountAsync(ct) : await DbSet.CountAsync(predicate, ct);

    public virtual void Add(T entity) => DbSet.Add(entity);
    public virtual void Update(T entity) => DbSet.Update(entity);
    public virtual void Remove(T entity) => DbSet.Remove(entity);

    public virtual async Task SaveChangesAsync(CancellationToken ct = default)
        => await Context.SaveChangesAsync(ct);
}
