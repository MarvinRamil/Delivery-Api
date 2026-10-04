using System.Text.Json;
using BeeLogistics.Modules.Verification.Domain;

namespace BeeLogistics.Modules.Verification.Application.Services;

public sealed record DiditDecision(
    DriverVerificationStatus Status,
    double? FaceMatchScore,
    double? LivenessScore,
    string? PortraitImageUrl,
    string? FrontImageUrl,
    string? SelfieImageUrl,
    string? IdDocumentType,
    string? IdNumber,
    string? FullName,
    string? DateOfBirth,
    string? StatusReason);

/// <summary>
/// Defensive parser for Didit session/decision payloads. Didit nests results per feature
/// (id verification, liveness, face match); block names vary between singular objects and
/// plural arrays across API versions, so both shapes are accepted.
/// </summary>
public static class DiditDecisionParser
{
    public static DriverVerificationStatus MapStatus(string? diditStatus) => diditStatus?.Trim().ToLowerInvariant() switch
    {
        "approved" => DriverVerificationStatus.Approved,
        "declined" => DriverVerificationStatus.Declined,
        "in review" => DriverVerificationStatus.InReview,
        "in progress" or "awaiting user" or "resubmitted" => DriverVerificationStatus.InProgress,
        "abandoned" => DriverVerificationStatus.Abandoned,
        "expired" or "kyc expired" => DriverVerificationStatus.Expired,
        _ => DriverVerificationStatus.Pending
    };

    public static DiditDecision Parse(JsonElement root)
    {
        var status = MapStatus(GetString(root, "status"));

        var idBlock = GetBlock(root, "id_verification", "id_verifications");
        var livenessBlock = GetBlock(root, "liveness", "liveness_checks");
        var faceMatchBlock = GetBlock(root, "face_match", "face_matches");

        string? fullName = idBlock is { } id1 ? GetString(id1, "full_name") : null;
        if (fullName == null && idBlock is { } id2)
        {
            var first = GetString(id2, "first_name");
            var last = GetString(id2, "last_name");
            if (first != null || last != null)
                fullName = $"{first} {last}".Trim();
        }

        return new DiditDecision(
            Status: status,
            FaceMatchScore: faceMatchBlock is { } fm ? GetDouble(fm, "score") : null,
            LivenessScore: livenessBlock is { } lv ? GetDouble(lv, "score") : null,
            PortraitImageUrl: idBlock is { } id3 ? GetString(id3, "portrait_image") : null,
            FrontImageUrl: idBlock is { } id7 ? GetString(id7, "front_image") : null,
            SelfieImageUrl: livenessBlock is { } lv2 ? GetString(lv2, "reference_image") : null,
            IdDocumentType: idBlock is { } id4 ? GetString(id4, "document_type") : null,
            IdNumber: idBlock is { } id5 ? GetString(id5, "document_number") : null,
            FullName: fullName,
            DateOfBirth: idBlock is { } id6 ? GetString(id6, "date_of_birth") : null,
            StatusReason: GetReason(root));
    }

    private static JsonElement? GetBlock(JsonElement root, string singular, string plural)
    {
        if (root.TryGetProperty(singular, out var obj) && obj.ValueKind == JsonValueKind.Object)
            return obj;
        if (root.TryGetProperty(plural, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            return arr[0];
        return null;
    }

    private static string? GetReason(JsonElement root)
    {
        // Didit reports reasons per feature block and/or as a top-level "reviews"/"reason" field.
        if (GetString(root, "reason") is { } r)
            return r;
        if (root.TryGetProperty("reviews", out var reviews) && reviews.ValueKind == JsonValueKind.Array && reviews.GetArrayLength() > 0)
            return GetString(reviews[0], "comment") ?? GetString(reviews[0], "reason");
        return null;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static double? GetDouble(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetDouble()
            : null;
}
