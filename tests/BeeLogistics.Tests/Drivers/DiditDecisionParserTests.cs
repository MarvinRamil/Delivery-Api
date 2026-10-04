using System.Text.Json;
using BeeLogistics.Modules.Verification.Application.Services;
using BeeLogistics.Modules.Verification.Domain;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// The parser tolerates Didit's per-feature result blocks appearing as either a
/// singular object or a plural array, and maps its case-sensitive status strings.
/// </summary>
public class DiditDecisionParserTests
{
    [Theory]
    [InlineData("Approved", DriverVerificationStatus.Approved)]
    [InlineData("Declined", DriverVerificationStatus.Declined)]
    [InlineData("In Review", DriverVerificationStatus.InReview)]
    [InlineData("In Progress", DriverVerificationStatus.InProgress)]
    [InlineData("Abandoned", DriverVerificationStatus.Abandoned)]
    [InlineData("Kyc Expired", DriverVerificationStatus.Expired)]
    [InlineData("something-else", DriverVerificationStatus.Pending)]
    public void MapStatus_maps_didit_strings(string didit, DriverVerificationStatus expected)
    {
        Assert.Equal(expected, DiditDecisionParser.MapStatus(didit));
    }

    [Fact]
    public void Parse_reads_scores_and_id_fields_from_singular_blocks()
    {
        const string json = """
        {
          "status": "Approved",
          "id_verification": {
            "document_type": "Driver License",
            "document_number": "N01-23-456789",
            "first_name": "Juan",
            "last_name": "Dela Cruz",
            "date_of_birth": "1990-01-02",
            "portrait_image": "https://media.didit/portrait.jpg",
            "front_image": "https://media.didit/front.jpg"
          },
          "liveness": { "score": 98.5, "reference_image": "https://media.didit/selfie.jpg" },
          "face_match": { "score": 91.2 }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var decision = DiditDecisionParser.Parse(doc.RootElement);

        Assert.Equal(DriverVerificationStatus.Approved, decision.Status);
        Assert.Equal(91.2, decision.FaceMatchScore);
        Assert.Equal(98.5, decision.LivenessScore);
        Assert.Equal("Driver License", decision.IdDocumentType);
        Assert.Equal("N01-23-456789", decision.IdNumber);
        Assert.Equal("Juan Dela Cruz", decision.FullName);
        Assert.Equal("1990-01-02", decision.DateOfBirth);
        Assert.Equal("https://media.didit/portrait.jpg", decision.PortraitImageUrl);
        Assert.Equal("https://media.didit/front.jpg", decision.FrontImageUrl);
        Assert.Equal("https://media.didit/selfie.jpg", decision.SelfieImageUrl);
    }

    [Fact]
    public void Parse_reads_from_plural_array_blocks()
    {
        const string json = """
        {
          "status": "Approved",
          "id_verifications": [ { "full_name": "Maria Santos", "document_number": "X1" } ],
          "face_matches": [ { "score": 88 } ]
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var decision = DiditDecisionParser.Parse(doc.RootElement);

        Assert.Equal("Maria Santos", decision.FullName);
        Assert.Equal("X1", decision.IdNumber);
        Assert.Equal(88, decision.FaceMatchScore);
    }

    [Fact]
    public void Parse_handles_missing_blocks_gracefully()
    {
        using var doc = JsonDocument.Parse("""{"status":"In Progress"}""");

        var decision = DiditDecisionParser.Parse(doc.RootElement);

        Assert.Equal(DriverVerificationStatus.InProgress, decision.Status);
        Assert.Null(decision.FaceMatchScore);
        Assert.Null(decision.FullName);
        Assert.Null(decision.PortraitImageUrl);
        Assert.Null(decision.FrontImageUrl);
    }
}
