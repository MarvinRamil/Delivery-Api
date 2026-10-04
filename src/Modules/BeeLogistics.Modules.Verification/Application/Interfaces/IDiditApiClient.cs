using System.Text.Json;

namespace BeeLogistics.Modules.Verification.Application.Interfaces;

public sealed record DiditSessionCreated(string SessionId, string Url);

/// <summary>
/// Client for the Didit identity-verification API (https://verification.didit.me).
/// Sessions cover ID document scan + selfie + passive liveness + face match.
/// </summary>
public interface IDiditApiClient
{
    bool Enabled { get; }

    /// <summary>Create a hosted verification session. vendorData is our ApplicationUser.Id.
    /// Pass <paramref name="workflowId"/> to run a specific Didit workflow (e.g. the customer flow);
    /// null/empty uses the default driver <c>WorkflowId</c>.</summary>
    Task<DiditSessionCreated> CreateSessionAsync(string vendorData, string? workflowId = null, CancellationToken cancellationToken = default);

    /// <summary>Fetch the full decision for a session (scores, extracted ID fields, fresh media URLs). Null if not available yet.</summary>
    Task<JsonDocument?> GetDecisionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Download a (short-lived, presigned) media URL from a decision payload.</summary>
    Task<Stream?> DownloadMediaAsync(string url, CancellationToken cancellationToken = default);
}
