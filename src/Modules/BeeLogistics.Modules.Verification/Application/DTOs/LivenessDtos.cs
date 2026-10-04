namespace BeeLogistics.Modules.Verification.Application.DTOs;

/// <summary>Direction for the head-pose challenge (e.g. look left, right, up, down).</summary>
public static class LivenessDirection
{
    public const string Left = "left";
    public const string Right = "right";
    public const string Up = "up";
    public const string Down = "down";

    public static readonly IReadOnlyList<string> All = new[] { Left, Right, Up, Down };

    public static string[] RandomSequence(int count = 4)
    {
        var rng = new Random();
        var indices = Enumerable.Range(0, All.Count).OrderBy(_ => rng.Next()).Take(count).ToArray();
        return indices.Select(i => All[i]).ToArray();
    }
}

public record CreateLivenessSessionResult(string SessionId, string[] Directions, DateTime ExpiresAt);

public record SubmitLivenessImageResult(bool DirectionPassed, bool AllPassed, string[] RemainingDirections, string? Error = null);

public record LivenessSessionStatusResult(string Status, string[] DirectionsPassed, string[] RemainingDirections)
{
    /// <summary>Status: "InProgress" | "Passed" | "Failed" | "Expired" | "NotFound"</summary>
    public const string InProgress = "InProgress";
    public const string Passed = "Passed";
    public const string Failed = "Failed";
    public const string Expired = "Expired";
    public const string NotFound = "NotFound";
}
