using BeeLogistics.Modules.Bookings.Application.Handlers;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Presentation.Images;
using BeeLogistics.Modules.Bookings.Presentation.Uploads;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace BeeLogistics.Modules.Bookings.Presentation.Controllers;

/// <summary>
/// Multipart uploads attached to an existing booking: proof of delivery and item image.
/// Split out of BookingsController.
/// </summary>
/// <remarks>
/// The explicit route is required — see <see cref="BookingImagesController"/>.
/// </remarks>
[Route("api/bookings")]
public class BookingUploadsController : BaseController
{
    private readonly IMediator _mediator;
    private readonly IBookingRepository _bookingRepository;
    private readonly IImageProcessor _imageProcessor;
    private readonly IFileStorageService _fileStorageService;
    private readonly long _maxFileSizeBytes;
    private readonly BookingUploadGuard _uploadGuard;
    private readonly BookingImageStorage _imageStorage;
    private readonly BookingImageAccessPolicy _accessPolicy;

    public BookingUploadsController(
        IMediator mediator,
        UserManager<ApplicationUser> userManager,
        IBookingRepository bookingRepository,
        IVirusScanner virusScanner,
        IImageProcessor imageProcessor,
        IFileStorageService fileStorageService,
        IConfiguration configuration,
        ILogger<BookingUploadsController> logger)
    {
        _mediator = mediator;
        _bookingRepository = bookingRepository;
        _imageProcessor = imageProcessor;
        _fileStorageService = fileStorageService;
        _maxFileSizeBytes = configuration.GetValue<long>("FileUpload:MaxFileSizeBytes", 10_485_760); // Default 10MB
        _uploadGuard = new BookingUploadGuard(virusScanner, configuration, _maxFileSizeBytes, logger);
        _imageStorage = new BookingImageStorage(imageProcessor, fileStorageService);
        _accessPolicy = new BookingImageAccessPolicy(userManager, mediator);
    }

    [HttpPost("{id:guid}/stops/{stopId:guid}/pod")]
    [Authorize(Roles = "Driver")]
    [RequestSizeLimit(11_000_000)] // 11MB (10MB image + 1MB form)
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadPod(
        Guid id,
        Guid stopId,
        [FromForm] UploadPodRequest request,
        CancellationToken ct = default)
    {
        var driverId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? Guid.Empty.ToString());

        // POD image and signature optional; client can enforce required validation
        string? imagePath = null;
        string? signaturePath = null;

        if (request.Image != null && request.Image.Length > 0)
        {
            // NOTE: integer division, so the number in this message has always been truncated
            var imageRejection = await _uploadGuard.CheckAsync(request.Image, new UploadGuardMessages(
                TooLarge: $"Image size exceeds maximum allowed ({_maxFileSizeBytes / (1024 * 1024)} MB).",
                WrongType: "Only JPG and PNG images are allowed for the delivery photo.",
                Infected: "File upload rejected due to security threat.",
                ScanFailed: "Virus scan failed. Please try again.",
                IncludeVirusName: false), ct);
            if (imageRejection != null)
                return BadRequest(new { success = false, message = imageRejection });

            await using (var inputStream = request.Image.OpenReadStream())
            await using (var processedStream = new MemoryStream())
            {
                var processingResult = await _imageProcessor.ProcessImageAsync(inputStream, processedStream, ct);
                if (!processingResult.Success)
                    return BadRequest(new { success = false, message = $"Image processing failed: {processingResult.ErrorMessage}" });
                processedStream.Position = 0;
                // Same folder as item-image: deliveries/{bookingId}/ (POD under proofOfDelivery subfolder)
                var podObjectName = $"{id:N}/proofOfDelivery/{stopId:N}-{Guid.NewGuid():N}.webp";
                var pathOrUrl = await _fileStorageService.UploadFileAsync(processedStream, BookingImageStorage.DeliveriesBucket, podObjectName, "image/webp", ct);
                imagePath = pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? $"s3:{BookingImageStorage.DeliveriesBucket}:{podObjectName}"
                    : pathOrUrl;
            }
        }

        if (request.Signature != null && request.Signature.Length > 0)
        {
            // scanForViruses: false preserves this path exactly - it has never been scanned.
            var signatureRejection = await _uploadGuard.CheckAsync(request.Signature, new UploadGuardMessages(
                TooLarge: "Signature image size exceeds maximum allowed.",
                WrongType: "Only JPG and PNG images are allowed for the signature.",
                Infected: "File upload rejected due to security threat.",
                ScanFailed: "Virus scan failed. Please try again."), ct, scanForViruses: false);
            if (signatureRejection != null)
                return BadRequest(new { success = false, message = signatureRejection });
            await using (var inputStream = request.Signature.OpenReadStream())
            await using (var processedStream = new MemoryStream())
            {
                var processingResult = await _imageProcessor.ProcessImageAsync(inputStream, processedStream, ct);
                if (!processingResult.Success)
                    return BadRequest(new { success = false, message = "Signature image processing failed." });
                processedStream.Position = 0;
                var sigObjectName = $"{id:N}/proofOfDelivery/{stopId:N}-signature-{Guid.NewGuid():N}.webp";
                var pathOrUrl = await _fileStorageService.UploadFileAsync(processedStream, BookingImageStorage.DeliveriesBucket, sigObjectName, "image/webp", ct);
                signaturePath = pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? $"s3:{BookingImageStorage.DeliveriesBucket}:{sigObjectName}"
                    : pathOrUrl;
            }
        }

        var command = new UploadPodCommand(
            id,
            stopId,
            driverId,
            imagePath,
            signaturePath,
            request.RecipientName,
            request.Notes
        );
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    public record UploadPodRequest(
        IFormFile? Image = null,
        IFormFile? Signature = null,
        string? RecipientName = null,
        string? Notes = null
    );

    /// <summary>
    /// Upload or replace item image for a booking (e.g. after creating via POST /api/bookings/multi-stop).
    /// Same validation and storage as legacy POST /api/bookings itemImage: size, image-only, virus scan; stored as deliveries/{bookingId}/item-image.webp.
    /// SECURITY: Only customer who owns the booking or backoffice roles can upload.
    /// </summary>
    [HttpPost("{id:guid}/item-image")]
    [Authorize]
    [RequestSizeLimit(11_000_000)] // 11MB max (10MB file + 1MB form overhead)
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadItemImage(Guid id, IFormFile? itemImage, CancellationToken ct = default)
    {
        var booking = await _bookingRepository.GetByIdAsync(id, ct);
        if (booking == null)
            return NotFound(new { success = false, message = "Booking not found" });

        if (!await _accessPolicy.CanAccessAsync(User, id, ct))
            return Forbid("You do not have access to this booking");

        if (itemImage == null || itemImage.Length == 0)
            return BadRequest(new { success = false, message = "Item image file is required (form field: itemImage)" });

        var rejection = await _uploadGuard.CheckAsync(itemImage, new UploadGuardMessages(
            TooLarge: $"File size exceeds maximum allowed size of {_uploadGuard.MaxFileSizeMb:F1} MB.",
            WrongType: "Only JPG and PNG image files are allowed. File content must match the file type.",
            Infected: "File upload rejected due to security threat.",
            ScanFailed: "File upload rejected: Virus scan failed. Please try again or contact support."), ct);
        if (rejection != null)
            return BadRequest(new { success = false, message = rejection });

        try
        {
            var itemImagePath = await _imageStorage.SaveItemImageAsync(itemImage, id, ct);
            var attached = await _mediator.Send(new AttachItemImageCommand(id, itemImagePath), ct);
            if (!attached.IsSuccess)
                return StatusCode(500, new { success = false, message = "Failed to save item image. Please try again." });

            return Ok(new { success = true, message = "Item image uploaded.", data = new { itemImagePath = attached.Value } });
        }
        catch (Exception)
        {
            return StatusCode(500, new { success = false, message = "Failed to save item image. Please try again." });
        }
    }
}
