using BeeLogistics.Modules.Verification.Application.DTOs;

namespace BeeLogistics.Modules.Verification.Application.Interfaces;

/// <summary>
/// Customer identity verification (Didit KYC), persisted to the separate CustomerVerifications
/// table. Same hosted flow as the driver <see cref="IKycService"/>, but customer-scoped and
/// without driver-only side effects (no reference embedding, no back-office driver event).
/// </summary>
public interface ICustomerKycService
{
    /// <summary>Create a new Didit verification session for the customer. Throws InvalidOperationException if already approved.</summary>
    Task<CreateKycSessionResult> CreateSessionAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Latest verification status for the customer; polls Didit as a fallback when a webhook has not landed yet.</summary>
    Task<KycStatusResult> GetStatusAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Apply a Didit webhook notification (already signature-verified by the caller). vendorData is the raw "customer:{userId}" tag.</summary>
    Task ProcessWebhookAsync(string sessionId, string? vendorData, string? diditStatus, CancellationToken cancellationToken = default);
}
