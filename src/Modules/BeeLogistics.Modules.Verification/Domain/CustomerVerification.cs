namespace BeeLogistics.Modules.Verification.Domain;

/// <summary>
/// One Didit KYC attempt (ID document + selfie + liveness + face match) for a CUSTOMER.
/// Mirrors <see cref="DriverVerification"/> but omits the driver-only reference embedding
/// (customers have no per-shift face checks). The latest row per user is the customer's
/// current verification state; older rows are audit history. Reuses
/// <see cref="DriverVerificationStatus"/> for status values.
/// </summary>
public class CustomerVerification
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

    /// <summary>Storage path of the verified portrait ("s3:bucket:key" or local path) — kept for audit.</summary>
    public string? ReferenceSelfiePath { get; set; }

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
