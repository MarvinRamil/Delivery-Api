using BeeLogistics.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BeeLogistics.Shared.Infrastructure.Security;

/// <summary>
/// Keeps deterministic blind-index columns in sync with their encrypted source property on every
/// save. For each configured mapping, the hash (shadow) column is recomputed from the entity's
/// plaintext value before it is written, so encrypted fields stay searchable via equality.
///
/// Mappings are configured by entity type NAME (string) to keep this Shared component decoupled
/// from the feature modules that own the entities.
/// </summary>
public sealed class BlindIndexSaveChangesInterceptor : SaveChangesInterceptor
{
    /// <param name="EntityTypeName">CLR type name of the entity (e.g. "ApplicationUser").</param>
    /// <param name="SourceProperty">Plaintext property to hash (e.g. "PhoneNumber").</param>
    /// <param name="HashProperty">Shadow column receiving the blind index (e.g. "PhoneNumberHash").</param>
    /// <param name="Field">Field tag passed to the blind-index normalizer (e.g. "phone").</param>
    public sealed record Mapping(string EntityTypeName, string SourceProperty, string HashProperty, string Field);

    private readonly IDataProtectorService _protector;
    private readonly IReadOnlyList<Mapping> _mappings;

    public BlindIndexSaveChangesInterceptor(IDataProtectorService protector, IEnumerable<Mapping> mappings)
    {
        _protector = protector;
        _mappings = mappings.ToList();
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null) return;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added && entry.State != EntityState.Modified) continue;

            var typeName = entry.Metadata.ClrType.Name;
            foreach (var map in _mappings)
            {
                if (!string.Equals(typeName, map.EntityTypeName, StringComparison.Ordinal)) continue;

                var source = entry.Properties.FirstOrDefault(p => p.Metadata.Name == map.SourceProperty);
                if (source is null) continue;

                var value = source.CurrentValue as string;
                entry.Property(map.HashProperty).CurrentValue =
                    string.IsNullOrEmpty(value) ? null : _protector.ComputeBlindIndex(value, map.Field);
            }
        }
    }
}
