namespace BeeLogistics.Modules.Accounting.Domain;

/// <summary>
/// Immutable ledger entry for auditing and reconciliation.
/// Append-only; entries are never updated or deleted.
/// </summary>
public class LedgerEntry
{
    public Guid Id { get; private set; }
    public string AccountCode { get; private set; } = null!;
    public bool IsDebit { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "PHP";
    public string ReferenceType { get; private set; } = null!; // e.g. "Withdrawal", "Payment"
    public string ReferenceId { get; private set; } = null!;
    public string? Description { get; private set; }
    public Guid? ActorId { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private LedgerEntry() { }

    public static LedgerEntry Create(
        string accountCode,
        bool isDebit,
        decimal amount,
        string currency,
        string referenceType,
        string referenceId,
        string? description = null,
        Guid? actorId = null,
        string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(accountCode))
            throw new ArgumentException("Account code is required", nameof(accountCode));
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));
        if (string.IsNullOrWhiteSpace(referenceType) || string.IsNullOrWhiteSpace(referenceId))
            throw new ArgumentException("Reference type and id are required", nameof(referenceType));

        return new LedgerEntry
        {
            Id = Guid.NewGuid(),
            AccountCode = accountCode,
            IsDebit = isDebit,
            Amount = amount,
            Currency = currency ?? "PHP",
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            Description = description,
            ActorId = actorId,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
