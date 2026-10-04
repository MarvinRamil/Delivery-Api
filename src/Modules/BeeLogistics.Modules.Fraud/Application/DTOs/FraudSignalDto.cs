namespace BeeLogistics.Modules.Fraud.Application.DTOs;

public record FraudSignalDto(
    Guid Id,
    string RuleName,
    Guid ActorId,
    string Severity,
    DateTime DetectedAt,
    string? MetadataJson,
    DateTime? ResolvedAt,
    string? Label,
    DateTime CreatedAt
);
