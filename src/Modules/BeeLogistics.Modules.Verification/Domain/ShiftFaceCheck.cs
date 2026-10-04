namespace BeeLogistics.Modules.Verification.Domain;

/// <summary>
/// Audit record of one per-shift face check attempt (selfie matched against the
/// driver's reference embedding before going online).
/// </summary>
public class ShiftFaceCheck
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;

    /// <summary>Cosine similarity of the best frame vs the reference embedding (0–1).</summary>
    public double? MatchScore { get; set; }

    public bool Passed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
