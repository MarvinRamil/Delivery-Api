using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Fraud.Domain;

/// <summary>
/// Result of a fraud rule (suspicious activity). Used for audit and future ML labeling.
/// </summary>
public class FraudSignal : Entity
{
    public string RuleName { get; private set; } = null!;
    public Guid ActorId { get; private set; }
    public string Severity { get; private set; } = "Suspicious";
    public DateTime DetectedAt { get; private set; }
    public string? MetadataJson { get; private set; }

    public DateTime? ResolvedAt { get; private set; }
    public string? Label { get; private set; }

    private FraudSignal() { }

    public static FraudSignal Create(string ruleName, Guid actorId, string? metadataJson = null)
    {
        return new FraudSignal
        {
            Id = Guid.NewGuid(),
            RuleName = ruleName,
            ActorId = actorId,
            Severity = "Suspicious",
            DetectedAt = DateTime.UtcNow,
            MetadataJson = metadataJson,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void Resolve(string label)
    {
        Label = label;
        ResolvedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
}
