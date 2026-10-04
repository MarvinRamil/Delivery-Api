namespace BeeLogistics.Modules.Verification.Application.Interfaces;

/// <summary>Raw ID-document image bytes extracted by Didit. Carries PII (the ID photo) —
/// unlike <see cref="DriverVerificationSummary"/>, do not surface this outside a trusted caller.</summary>
public sealed record DriverIdDocumentImage(Stream Content, string ContentType);

/// <summary>
/// Read-only cross-module access to the ID document portrait Didit extracted during KYC,
/// consumed by the Drivers module to populate the license image without a manual upload.
/// </summary>
public interface IDriverIdDocumentProvider
{
    /// <summary>Fetches the ID-document portrait image Didit extracted for the driver's latest
    /// KYC session, once one exists — i.e. Status is Approved or InReview ("needs a manual
    /// check" on Didit's side; the ID document scan has already run by that point). Null while
    /// still Pending/InProgress (nothing scanned yet), after Declined/Abandoned/Expired, or if
    /// Didit has no image available.</summary>
    Task<DriverIdDocumentImage?> GetLatestAvailableIdImageAsync(string userId, CancellationToken cancellationToken = default);
}
