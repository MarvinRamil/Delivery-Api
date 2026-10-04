namespace BeeLogistics.Modules.Verification.Domain;

public enum DriverVerificationStatus
{
    /// <summary>Session created, user has not finished the hosted flow.</summary>
    Pending = 0,
    InProgress = 1,
    InReview = 2,
    Approved = 3,
    Declined = 4,
    Abandoned = 5,
    Expired = 6,
    /// <summary>Backfilled for drivers verified under the old liveness-only flow (no Didit session).</summary>
    Legacy = 7
}

/// <summary>
/// One Didit KYC attempt (ID document + selfie + liveness + face match) for a driver.
/// The latest row per user is the driver's current verification state; older rows are audit history.
/// </summary>
public class DriverVerification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>ApplicationUser.Id (string, max 450 like other modules).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Didit session_id; null for Legacy rows.</summary>
    public string? DiditSessionId { get; set; }

    public DriverVerificationStatus Status { get; set; } = DriverVerificationStatus.Pending;

    /// <summary>Didit face match similarity 0–100 (selfie vs ID portrait).</summary>
    public double? FaceMatchScore { get; set; }

    /// <summary>Didit liveness confidence 0–100.</summary>
    public double? LivenessScore { get; set; }

    /// <summary>Storage path of the verified portrait ("s3:bucket:key" or local path) — the trusted reference selfie.</summary>
    public string? ReferenceSelfiePath { get; set; }

    /// <summary>Face embedding of the reference selfie (JSON float array), used by per-shift checks. Null until computed.</summary>
    public string? ReferenceEmbedding { get; set; }

    // Extracted ID document data (PII, encrypted at rest via VerificationDbContext converters)
    public string? IdDocumentType { get; set; }
    public string? IdNumber { get; set; }
    public string? FullName { get; set; }
    public string? DateOfBirth { get; set; }

    /// <summary>Didit decline/review reason when Status is Declined or InReview.</summary>
    public string? StatusReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
