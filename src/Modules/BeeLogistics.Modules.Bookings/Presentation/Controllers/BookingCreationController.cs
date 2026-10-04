using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Handlers;
using BeeLogistics.Modules.Bookings.Application.Services;
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
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;

namespace BeeLogistics.Modules.Bookings.Presentation.Controllers;

/// <summary>
/// Booking creation. One endpoint, accepting JSON or multipart.
/// Split out of BookingsController.
/// </summary>
/// <remarks>
/// The explicit route is required — see <see cref="BookingImagesController"/>.
/// </remarks>
[Route("api/bookings")]
public class BookingCreationController : BaseController
{
    private readonly IMediator _mediator;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IFileStorageService _fileStorageService;
    private readonly ILogger<BookingCreationController> _logger;
    private readonly BookingUploadGuard _uploadGuard;
    private readonly BookingImageStorage _imageStorage;

    public BookingCreationController(
        IMediator mediator,
        UserManager<ApplicationUser> userManager,
        IVirusScanner virusScanner,
        IImageProcessor imageProcessor,
        IFileStorageService fileStorageService,
        IConfiguration configuration,
        ILogger<BookingCreationController> logger)
    {
        _mediator = mediator;
        _userManager = userManager;
        _fileStorageService = fileStorageService;
        _logger = logger;
        var maxFileSizeBytes = configuration.GetValue<long>("FileUpload:MaxFileSizeBytes", 10_485_760); // Default 10MB
        _uploadGuard = new BookingUploadGuard(virusScanner, configuration, maxFileSizeBytes, logger);
        _imageStorage = new BookingImageStorage(imageProcessor, fileStorageService);
    }

    /// <summary>
    /// Create a booking: exactly one pickup and one dropoff.
    /// Accepts either JSON (application/json) or multipart (multipart/form-data).
    /// Multipart: form field "payload" = JSON string of the booking payload; optional
    /// "itemImage" = file.
    /// </summary>
    /// <remarks>
    /// The single creation endpoint. It replaced two that had drifted apart — a flat
    /// pickup/dropoff multipart form that never set a fare, and this stops-based one that no
    /// client ever called. The flat shape is gone; senders build the two stops explicitly.
    /// </remarks>
    [HttpPost]
    [Authorize]
    [RequestSizeLimit(11_000_000)] // 11MB when sending item image
    [Consumes("application/json", "multipart/form-data")]
    public async Task<IActionResult> Create(CancellationToken ct = default)
    {
        CreateBookingDto? dto;
        IFormFile? itemImage = null;
        var contentType = Request.ContentType ?? "";

        if (contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            var form = await Request.ReadFormAsync(ct);
            var payload = form["payload"].ToString();
            if (string.IsNullOrWhiteSpace(payload))
                return BadRequest(new { success = false, message = "Multipart request must include form field 'payload' (JSON string of the booking)." });
            try
            {
                dto = JsonSerializer.Deserialize<CreateBookingDto>(payload, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
            }
            catch (JsonException)
            {
                return BadRequest(new { success = false, message = "Invalid JSON in payload field." });
            }
            itemImage = form.Files.GetFile("itemImage");
        }
        else
        {
            try
            {
                dto = await JsonSerializer.DeserializeAsync<CreateBookingDto>(Request.Body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }, ct);
            }
            catch (JsonException)
            {
                return BadRequest(new { success = false, message = "Invalid JSON body." });
            }
        }

        if (dto == null)
            return BadRequest(new { success = false, message = "Booking payload is required (JSON body or multipart form field 'payload')." });

        // Resolve the user from the token id first (works for both legacy and Clerk
        // tokens — Clerk doesn't emit the ClaimTypes.Email schema claim), then fall
        // back to email claims under their various names. Without this, Clerk-created
        // accounts have no email here and booking fails with "Customer not found".
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        var appUser = string.IsNullOrEmpty(userId) ? null : await _userManager.FindByIdAsync(userId);
        var userEmail = appUser?.Email
            ?? User.FindFirstValue(ClaimTypes.Email)
            ?? User.FindFirstValue(JwtRegisteredClaimNames.Email)
            ?? User.FindFirst("email")?.Value;
        var userFullName = appUser?.FullName ?? User.FindFirstValue("full_name");

        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized(new { success = false, message = "Could not resolve your account email from the session. Please sign in again." });

        if (itemImage != null)
        {
            var rejection = await _uploadGuard.CheckAsync(itemImage, new UploadGuardMessages(
                TooLarge: $"Item image exceeds maximum size of {_uploadGuard.MaxFileSizeMb:F1} MB.",
                WrongType: "Only JPG and PNG image files are allowed. File content must match the file type.",
                Infected: "File upload rejected due to security threat.",
                ScanFailed: "File upload rejected: Virus scan failed. Please try again or contact support."), ct);
            if (rejection != null)
                return BadRequest(new { success = false, message = rejection });
        }

        var command = new CreateBookingCommand(dto, userEmail, userFullName);
        var result = await _mediator.Send(command, ct);

        if (result.IsSuccess && result.Value != null && itemImage != null)
        {
            try
            {
                var bookingId = result.Value.Id;
                var itemImagePath = await _imageStorage.SaveItemImageAsync(itemImage, bookingId, ct);
                var attached = await _mediator.Send(new AttachItemImageCommand(bookingId, itemImagePath), ct);
                // Unlike Create, this path answers Ok even when the booking could not be
                // re-read - the image URL is resolved either way.
                var viewableImageUrl = attached.IsSuccess
                    ? attached.Value
                    : await BookingImageUrlResolver.ResolveStoragePathAsync(itemImagePath, _fileStorageService, ct: ct);
                var updatedDto = result.Value with { ItemImagePath = viewableImageUrl };
                return Ok(new { success = true, data = updatedDto });
            }
            catch
            {
                // Booking already created; return success without image path
            }
        }

        return FromResult(result);
    }
}
