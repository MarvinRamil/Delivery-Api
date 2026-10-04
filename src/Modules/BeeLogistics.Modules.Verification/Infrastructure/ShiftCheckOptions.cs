namespace BeeLogistics.Modules.Verification.Infrastructure;

public class ShiftCheckOptions
{
    public const string SectionName = "ShiftCheck";

    /// <summary>Gates going online behind a face check. Requires FaceMatch:Enabled.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>How long a passed check stays valid before the driver must re-verify to go online.</summary>
    public int MaxAgeHours { get; set; } = 12;

    /// <summary>Head-pose photos per check (onboarding used 4; per-shift favors speed).</summary>
    public int DirectionCount { get; set; } = 2;

    public int SessionExpiryMinutes { get; set; } = 10;

    /// <summary>Also run each frame through the YOLO anti-spoofing service (when Liveness:Enabled).</summary>
    public bool UseLiveness { get; set; } = true;

    /// <summary>Shadow mode: run checks and record scores but never block going online.</summary>
    public bool ShadowMode { get; set; } = false;
}
