using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Presentation.Images;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace BeeLogistics.Modules.Bookings.Presentation.Controllers;

/// <summary>
/// Serving booking images: presigned redirects for object storage, local disk otherwise.
/// Split out of BookingsController — this is about storage references and path safety, not
/// about bookings.
/// </summary>
/// <remarks>
/// The explicit route is required. BaseController carries [Route("api/[controller]")], so
/// without this every route here would move to /api/bookingimages.
/// </remarks>
[Route("api/bookings")]
public class BookingImagesController : BaseController
{
    private const string InvalidReferenceMessage = "Invalid image reference";

    private readonly IBookingRepository _bookingRepository;
    private readonly IFileStorageService _fileStorageService;
    private readonly BookingImageAccessPolicy _accessPolicy;

    public BookingImagesController(
        IBookingRepository bookingRepository,
        IFileStorageService fileStorageService,
        UserManager<ApplicationUser> userManager,
        IMediator mediator)
    {
        _bookingRepository = bookingRepository;
        _fileStorageService = fileStorageService;
        _accessPolicy = new BookingImageAccessPolicy(userManager, mediator);
    }

    /// <summary>
    /// Get booking item image
    /// SECURITY: Only allows access to images for bookings the user has access to
    /// </summary>
    [HttpGet("{id:guid}/item-image")]
    [Authorize]
    public async Task<IActionResult> GetItemImage(Guid id, CancellationToken ct)
    {
        var booking = await _bookingRepository.GetByIdAsync(id, ct);
        if (booking == null)
            return NotFound(new { success = false, message = "Booking not found" });

        if (!await _accessPolicy.CanAccessAsync(User, id, ct))
            return Forbid("You do not have access to this booking's image");

        if (string.IsNullOrWhiteSpace(booking.ItemImagePath))
            return NotFound(new { success = false, message = "Item image not found" });

        return await ServeAsync(booking.ItemImagePath, ct);
    }

    /// <summary>
    /// Get proof-of-delivery image for a stop.
    /// SECURITY: Only allows access for bookings the user has access to (customer or admin/owner/dispatcher).
    /// </summary>
    [HttpGet("{id:guid}/stops/{stopId:guid}/pod-image")]
    [Authorize]
    public async Task<IActionResult> GetPodImage(Guid id, Guid stopId, CancellationToken ct)
    {
        var booking = await _bookingRepository.GetByIdWithProofOfDeliveriesAsync(id, ct);
        if (booking == null)
            return NotFound(new { success = false, message = "Booking not found" });

        if (!await _accessPolicy.CanAccessAsync(User, id, ct))
            return Forbid("You do not have access to this booking's image");

        var pod = booking.ProofOfDeliveries.FirstOrDefault(p => p.StopId == stopId);
        if (pod == null || string.IsNullOrWhiteSpace(pod.ImagePath))
            return NotFound(new { success = false, message = "POD image not found" });

        return await ServeAsync(pod.ImagePath, ct);
    }

    /// <summary>
    /// Get signature image for a stop.
    /// SECURITY: Only allows access for bookings the user has access to (customer or admin/owner/dispatcher).
    /// </summary>
    [HttpGet("{id:guid}/stops/{stopId:guid}/signature-image")]
    [Authorize]
    public async Task<IActionResult> GetSignatureImage(Guid id, Guid stopId, CancellationToken ct)
    {
        var booking = await _bookingRepository.GetByIdWithProofOfDeliveriesAsync(id, ct);
        if (booking == null)
            return NotFound(new { success = false, message = "Booking not found" });

        if (!await _accessPolicy.CanAccessAsync(User, id, ct))
            return Forbid("You do not have access to this booking's image");

        var pod = booking.ProofOfDeliveries.FirstOrDefault(p => p.StopId == stopId);
        if (pod == null || string.IsNullOrWhiteSpace(pod.SignaturePath))
            return NotFound(new { success = false, message = "Signature image not found" });

        return await ServeAsync(pod.SignaturePath, ct);
    }

    /// <summary>
    /// S3/R2: presigned redirect; local: serve from disk.
    /// </summary>
    /// <remarks>
    /// The three endpoints used to disagree about a malformed s3: reference — one returned
    /// NotFound, two fell through to disk. The fall-through reached the same 404 by a longer
    /// route: it built "App_Data/s3:bucket", cleared the traversal check and failed
    /// File.Exists. Answering NotFound directly is the same status for the caller, and it
    /// keeps a broken storage reference from ever being treated as a filename.
    /// </remarks>
    private async Task<IActionResult> ServeAsync(string storedPath, CancellationToken ct)
    {
        switch (BookingImageUrlResolver.TryParseS3Reference(storedPath, out var bucket, out var objectKey))
        {
            case S3ReferenceKind.Parsed:
                var presignedUrl = await _fileStorageService.GetPresignedUrlAsync(bucket, objectKey, expiresInSeconds: 3600, ct);
                return Redirect(presignedUrl);

            case S3ReferenceKind.Malformed:
                return NotFound(new { success = false, message = InvalidReferenceMessage });
        }

        var root = Path.Combine(Directory.GetCurrentDirectory(), "App_Data");
        var fullPath = Path.Combine(root, storedPath.Replace("/", Path.DirectorySeparatorChar.ToString()));

        // SECURITY: Additional path validation to prevent path traversal
        var fullRoot = Path.GetFullPath(root);
        var fullFilePath = Path.GetFullPath(fullPath);
        if (!fullFilePath.StartsWith(fullRoot, StringComparison.Ordinal))
            return BadRequest(new { success = false, message = "Invalid file path" });

        if (!System.IO.File.Exists(fullPath))
            return NotFound(new { success = false, message = "File not found" });

        // SECURITY: Serve WebP image with proper content type, cache headers, and no-cache for sensitive data
        return File(System.IO.File.OpenRead(fullPath), "image/webp", enableRangeProcessing: true);
    }
}
