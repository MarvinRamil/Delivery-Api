namespace BeeLogistics.Modules.Verification.Application.Interfaces;

/// <summary>
/// Client for the self-hosted InsightFace face-match service
/// (POST /v1/embed, POST /v1/verify — see bee-backend/docker/facematch).
/// </summary>
public interface IFaceMatchApiClient
{
    bool Enabled { get; }

    /// <summary>Compute the (L2-normalized) face embedding of the largest face in the image. Null if no face found.</summary>
    Task<float[]?> EmbedAsync(Stream imageStream, CancellationToken cancellationToken = default);

    /// <summary>Cosine similarity between the largest face in the image and the reference embedding. Null if no face found.</summary>
    Task<double?> VerifyAsync(Stream imageStream, float[] referenceEmbedding, CancellationToken cancellationToken = default);
}
