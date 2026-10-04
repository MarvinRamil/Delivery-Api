namespace BeeLogistics.Modules.Verification.Infrastructure;

public class LivenessOptions
{
    public const string SectionName = "Liveness";

    // Default off: KYC runs through the hosted Didit flow; enable only where a
    // self-hosted YOLO liveness service is actually deployed (Liveness__Enabled=true).
    public bool Enabled { get; set; } = false;
    public string BaseUrl { get; set; } = "http://anti-spoofing-api:8000";
    public int TimeoutSeconds { get; set; } = 30;
    public int SessionExpiryMinutes { get; set; } = 10;
}
