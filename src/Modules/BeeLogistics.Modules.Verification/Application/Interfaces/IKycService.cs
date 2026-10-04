using BeeLogistics.Modules.Verification.Application.DTOs;

namespace BeeLogistics.Modules.Verification.Application.Interfaces;

public interface IKycService
{
    /// <summary>Create a new Didit verification session for the driver. Throws InvalidOperationException if already approved.</summary>
    Task<CreateKycSessionResult> CreateSessionAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Latest verification status for the driver; polls Didit as a fallback when a webhook has not landed yet.</summary>
    Task<KycStatusResult> GetStatusAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Apply a Didit webhook notification (already signature-verified by the caller).</summary>
    Task ProcessWebhookAsync(string sessionId, string? vendorData, string? diditStatus, CancellationToken cancellationToken = default);
}
