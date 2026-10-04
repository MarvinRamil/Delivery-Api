namespace BeeLogistics.Modules.Verification.Application.DTOs;

public record CreateShiftCheckSessionResult(string SessionId, string[] Directions, DateTime ExpiresAt);

public record SubmitShiftCheckImageResult(
    bool DirectionPassed,
    bool AllPassed,
    string[] RemainingDirections,
    string? Error = null,
    double? MatchScore = null);
