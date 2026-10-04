namespace BeeLogistics.Modules.Verification.Infrastructure;

public class FaceMatchOptions
{
    public const string SectionName = "FaceMatch";

    public bool Enabled { get; set; } = false;
    public string BaseUrl { get; set; } = "http://facematch-api:8001";
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>Minimum cosine similarity (0–1) for a selfie to count as the same person. Calibrate in shadow mode before enforcing.</summary>
    public double MatchThreshold { get; set; } = 0.45;
}
