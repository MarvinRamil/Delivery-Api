using BeeLogistics.Modules.Bookings.Application.Services;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// Parsing of the stored "s3:bucket:objectKey" reference (issue #49).
///
/// The three-way result exists because the image endpoints have to tell a local path apart
/// from a broken s3 reference: the first is served from disk, the second must not be, and the
/// older code that returned the input unchanged on failure could not express the difference.
/// </summary>
public class S3ReferenceParsingTests
{
    [Fact]
    public void A_well_formed_reference_yields_bucket_and_key()
    {
        var kind = BookingImageUrlResolver.TryParseS3Reference(
            "s3:deliveries:abc123/item-image.webp", out var bucket, out var objectKey);

        Assert.Equal(S3ReferenceKind.Parsed, kind);
        Assert.Equal("deliveries", bucket);
        Assert.Equal("abc123/item-image.webp", objectKey);
    }

    [Fact]
    public void Colons_in_the_object_key_survive()
    {
        // Split(':', 3) caps the segment count, so everything after the bucket is the key.
        // A key containing a colon must not be truncated into a shorter object name.
        var kind = BookingImageUrlResolver.TryParseS3Reference(
            "s3:deliveries:folder/a:b:c.webp", out var bucket, out var objectKey);

        Assert.Equal(S3ReferenceKind.Parsed, kind);
        Assert.Equal("deliveries", bucket);
        Assert.Equal("folder/a:b:c.webp", objectKey);
    }

    [Theory]
    [InlineData("s3:deliveries")]
    [InlineData("s3:")]
    public void An_s3_reference_missing_its_key_is_malformed(string stored)
    {
        var kind = BookingImageUrlResolver.TryParseS3Reference(stored, out var bucket, out var objectKey);

        Assert.Equal(S3ReferenceKind.Malformed, kind);
        Assert.Equal(string.Empty, bucket);
        Assert.Equal(string.Empty, objectKey);
    }

    [Theory]
    [InlineData("uploads/bookings/abc.webp")]
    [InlineData("bookings/local.webp")]
    public void A_local_path_is_not_an_s3_reference(string stored)
    {
        // Must stay distinct from Malformed - these are still served from disk.
        Assert.Equal(S3ReferenceKind.NotS3, BookingImageUrlResolver.TryParseS3Reference(stored, out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_stored_is_not_an_s3_reference(string? stored)
    {
        Assert.Equal(S3ReferenceKind.NotS3, BookingImageUrlResolver.TryParseS3Reference(stored, out _, out _));
    }

    [Fact]
    public void The_prefix_match_is_case_insensitive()
    {
        Assert.Equal(S3ReferenceKind.Parsed,
            BookingImageUrlResolver.TryParseS3Reference("S3:deliveries:key.webp", out _, out _));
    }
}
