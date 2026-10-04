using BeeLogistics.Modules.Offers.Domain;
using BeeLogistics.Modules.Offers.Infrastructure;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Infrastructure;
using BeeLogistics.Shared.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Offers.Presentation.Controllers;

/// <summary>
/// App-facing promotional offers (read-only). Content is authored in the
/// back-office backend and pushed here via the integration endpoints below.
/// </summary>
[ApiController]
[Authorize]
[Route("api/offers")]
public class OffersController : ControllerBase
{
    private readonly IOfferRepository _repository;
    private readonly IFileStorageService _fileStorageService;

    public OffersController(IOfferRepository repository, IFileStorageService fileStorageService)
    {
        _repository = repository;
        _fileStorageService = fileStorageService;
    }

    [HttpGet("active")]
    public async Task<IActionResult> GetActive([FromQuery] OfferAudience? audience = null, CancellationToken ct = default)
    {
        var offers = await _repository.GetActiveAsync(audience, ct);
        var data = new List<object>(offers.Count);

        foreach (var offer in offers)
        {
            data.Add(new
            {
                offer.Id,
                offer.Title,
                offer.Description,
                offer.DiscountType,
                offer.DiscountValue,
                offer.PromoCode,
                offer.TargetAudience,
                offer.StartsAt,
                offer.EndsAt,
                ImageUrl = await OfferFileUrls.ResolveAsync(_fileStorageService, offer.ImagePath, ct),
            });
        }

        return Ok(new { success = true, data });
    }
}

/// <summary>
/// Integration surface for the back-office backend (ServiceApiKey S2S auth only).
/// PUT is an idempotent upsert — the back-office owns the offer id.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = ServiceApiKeyDefaults.SchemeName, Policy = "Backoffice")]
[Route("api/integration/offers")]
public class IntegrationOffersController : ControllerBase
{
    private const string OfferImageBucket = "offers";
    private static readonly FileUploadValidationOptions OfferImageValidation = FileUploadValidator.ImageOnlyOptions(5_000_000);

    private readonly IOfferRepository _repository;
    private readonly IFileStorageService _fileStorageService;
    private readonly IImageProcessor _imageProcessor;
    private readonly ILogger<IntegrationOffersController> _logger;

    public IntegrationOffersController(
        IOfferRepository repository,
        IFileStorageService fileStorageService,
        IImageProcessor imageProcessor,
        ILogger<IntegrationOffersController> logger)
    {
        _repository = repository;
        _fileStorageService = fileStorageService;
        _imageProcessor = imageProcessor;
        _logger = logger;
    }

    public class OfferUpsertForm
    {
        [FromForm(Name = "title")]
        public string Title { get; set; } = string.Empty;

        [FromForm(Name = "description")]
        public string Description { get; set; } = string.Empty;

        [FromForm(Name = "discountType")]
        public OfferDiscountType DiscountType { get; set; }

        [FromForm(Name = "discountValue")]
        public decimal DiscountValue { get; set; }

        [FromForm(Name = "promoCode")]
        public string? PromoCode { get; set; }

        [FromForm(Name = "targetAudience")]
        public OfferAudience TargetAudience { get; set; } = OfferAudience.All;

        [FromForm(Name = "startsAt")]
        public DateTime StartsAt { get; set; }

        [FromForm(Name = "endsAt")]
        public DateTime EndsAt { get; set; }

        [FromForm(Name = "isActive")]
        public bool IsActive { get; set; } = true;

        [FromForm(Name = "image")]
        public IFormFile? Image { get; set; }
    }

    [HttpPut("{id:guid}")]
    [RequestSizeLimit(6_000_000)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Upsert(Guid id, [FromForm] OfferUpsertForm form, CancellationToken ct = default)
    {
        string? imagePath = null;
        if (form.Image != null)
        {
            if (!await FileUploadValidator.ValidateAsync(form.Image, OfferImageValidation, ct))
            {
                return BadRequest(new { success = false, message = "Invalid image. Only JPG/PNG up to 5MB are allowed." });
            }

            imagePath = await UploadImageAsync(form.Image, $"offer-{id:N}", ct);
        }

        var existing = await _repository.GetByIdAsync(id, ct);
        try
        {
            if (existing == null)
            {
                var offer = new Offer(
                    id, form.Title, form.Description, form.DiscountType, form.DiscountValue,
                    form.PromoCode, form.TargetAudience, form.StartsAt, form.EndsAt,
                    imagePath, form.IsActive);
                _repository.Add(offer);
            }
            else
            {
                existing.Update(
                    form.Title, form.Description, form.DiscountType, form.DiscountValue,
                    form.PromoCode, form.TargetAudience, form.StartsAt, form.EndsAt,
                    imagePath ?? existing.ImagePath, form.IsActive);
            }
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }

        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Offer {OfferId} {Action} via integration API by {ClientId}",
            id, existing == null ? "created" : "updated", User.FindFirst("client_id")?.Value);

        var saved = existing ?? await _repository.GetByIdAsync(id, ct);
        return Ok(new
        {
            success = true,
            data = new
            {
                id,
                imageUrl = await OfferFileUrls.ResolveAsync(_fileStorageService, saved?.ImagePath, ct),
            }
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct = default)
    {
        var offer = await _repository.GetByIdAsync(id, ct);
        if (offer == null)
            return NotFound(new { success = false, message = "Offer not found." });

        offer.Deactivate();
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Offer {OfferId} deactivated via integration API by {ClientId}",
            id, User.FindFirst("client_id")?.Value);

        return Ok(new { success = true });
    }

    private async Task<string> UploadImageAsync(IFormFile image, string objectPrefix, CancellationToken ct)
    {
        await using var inputStream = image.OpenReadStream();
        await using var processedStream = new MemoryStream();
        var processed = await _imageProcessor.ProcessImageAsync(inputStream, processedStream, ct);

        if (processed.Success)
        {
            processedStream.Position = 0;
            var processedObject = $"{objectPrefix}.webp";
            var pathOrUrl = await _fileStorageService.UploadFileAsync(processedStream, OfferImageBucket, processedObject, "image/webp", ct);
            if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return $"s3:{OfferImageBucket}:{processedObject}";
            return pathOrUrl;
        }

        var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
        var objectName = $"{objectPrefix}{extension}";
        await using var fallback = image.OpenReadStream();
        var uploaded = await _fileStorageService.UploadFileAsync(fallback, OfferImageBucket, objectName, image.ContentType, ct);
        if (uploaded.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uploaded.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return $"s3:{OfferImageBucket}:{objectName}";
        return uploaded;
    }
}

internal static class OfferFileUrls
{
    public static async Task<string?> ResolveAsync(IFileStorageService fileStorage, string? storedPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
            return null;

        if (!storedPath.StartsWith("s3:", StringComparison.OrdinalIgnoreCase))
            return storedPath;

        var parts = storedPath.Split(':', 3, StringSplitOptions.None);
        if (parts.Length < 3)
            return null;

        return await fileStorage.GetPresignedUrlAsync(parts[1], parts[2], 7 * 24 * 3600, ct);
    }
}
