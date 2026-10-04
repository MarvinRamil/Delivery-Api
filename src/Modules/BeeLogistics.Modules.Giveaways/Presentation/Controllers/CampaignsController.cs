using BeeLogistics.Modules.Giveaways.Domain;
using BeeLogistics.Modules.Giveaways.Infrastructure;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Infrastructure;
using BeeLogistics.Shared.Presentation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BeeLogistics.Modules.Giveaways.Presentation.Controllers;

[ApiController]
[Authorize]
[Route("api/campaigns")]
public class CampaignsController : BaseController
{
    private const string CampaignImageBucket = "campaigns";
    private static readonly FileUploadValidationOptions CampaignImageValidation = FileUploadValidator.ImageOnlyOptions(5_000_000);

    private readonly IGiveawayRepository _repository;
    private readonly IFileStorageService _fileStorageService;
    private readonly IImageProcessor _imageProcessor;

    public CampaignsController(
        IGiveawayRepository repository,
        IFileStorageService fileStorageService,
        IImageProcessor imageProcessor)
    {
        _repository = repository;
        _fileStorageService = fileStorageService;
        _imageProcessor = imageProcessor;
    }

    public class CampaignUpsertForm
    {
        [FromForm(Name = "type")]
        public CampaignType Type { get; set; } = CampaignType.News;

        [FromForm(Name = "title")]
        public string Title { get; set; } = string.Empty;

        [FromForm(Name = "body")]
        public string Body { get; set; } = string.Empty;

        [FromForm(Name = "startDate")]
        public DateTime StartDate { get; set; }

        [FromForm(Name = "endDate")]
        public DateTime EndDate { get; set; }

        [FromForm(Name = "ctaText")]
        public string? CtaText { get; set; }

        [FromForm(Name = "ctaRoute")]
        public string? CtaRoute { get; set; }

        [FromForm(Name = "isActive")]
        public bool? IsActive { get; set; }

        [FromForm(Name = "image")]
        public IFormFile? Image { get; set; }
    }

    [HttpGet]
    [Authorize(Policy = "Backoffice")]
    public async Task<IActionResult> GetAll([FromQuery] bool activeOnly = false, CancellationToken ct = default)
    {
        var campaigns = await _repository.GetCampaignsAsync(activeOnly, ct);
        var data = new List<object>(campaigns.Count);

        foreach (var item in campaigns)
        {
            data.Add(new
            {
                item.Id,
                item.Type,
                item.Title,
                item.Body,
                item.StartDate,
                item.EndDate,
                item.CtaText,
                item.CtaRoute,
                item.IsActive,
                ImageUrl = await ResolveFileUrlAsync(item.ImagePath, ct),
                item.CreatedAt
            });
        }

        return Ok(new { success = true, data });
    }

    [HttpGet("active")]
    public async Task<IActionResult> GetActive(CancellationToken ct = default)
    {
        var campaigns = await _repository.GetCampaignsAsync(activeOnly: true, ct);
        var data = new List<object>(campaigns.Count);

        foreach (var item in campaigns)
        {
            data.Add(new
            {
                item.Id,
                item.Type,
                item.Title,
                item.Body,
                item.StartDate,
                item.EndDate,
                item.CtaText,
                item.CtaRoute,
                ImageUrl = await ResolveFileUrlAsync(item.ImagePath, ct)
            });
        }

        return Ok(new { success = true, data });
    }

    [HttpPost]
    [Authorize(Policy = "Backoffice")]
    [RequestSizeLimit(6_000_000)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Create([FromForm] CampaignUpsertForm form, CancellationToken ct = default)
    {
        string? imagePath = null;
        if (form.Image != null)
        {
            if (!await FileUploadValidator.ValidateAsync(form.Image, CampaignImageValidation, ct))
            {
                return BadRequest(new { success = false, message = "Invalid image. Only JPG/PNG up to 5MB are allowed." });
            }

            imagePath = await UploadImageAsync(form.Image, $"campaign-{Guid.NewGuid():N}", ct);
        }

        Campaign campaign;
        try
        {
            campaign = new Campaign(
                form.Type,
                form.Title,
                form.Body,
                form.StartDate,
                form.EndDate,
                imagePath,
                form.CtaText,
                form.CtaRoute);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }

        await _repository.AddCampaignAsync(campaign, ct);
        return Ok(new { success = true, data = new { campaign.Id } });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Backoffice")]
    [RequestSizeLimit(6_000_000)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Update(Guid id, [FromForm] CampaignUpsertForm form, CancellationToken ct = default)
    {
        var campaign = await _repository.GetCampaignByIdAsync(id, ct);
        if (campaign == null)
            return NotFound(new { success = false, message = "Campaign not found." });

        var imagePath = campaign.ImagePath;
        if (form.Image != null)
        {
            if (!await FileUploadValidator.ValidateAsync(form.Image, CampaignImageValidation, ct))
            {
                return BadRequest(new { success = false, message = "Invalid image. Only JPG/PNG up to 5MB are allowed." });
            }

            imagePath = await UploadImageAsync(form.Image, $"campaign-{id:N}", ct);
        }

        try
        {
            campaign.Update(
                form.Type,
                form.Title,
                form.Body,
                form.StartDate,
                form.EndDate,
                imagePath,
                form.CtaText,
                form.CtaRoute,
                form.IsActive ?? campaign.IsActive);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }

        await _repository.UpdateCampaignAsync(campaign, ct);
        return Ok(new { success = true });
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "Backoffice")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var campaign = await _repository.GetCampaignByIdAsync(id, ct);
        if (campaign == null)
            return NotFound(new { success = false, message = "Campaign not found." });

        var deletedBy = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _repository.SoftDeleteCampaignAsync(campaign, deletedBy, ct);
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
            var pathOrUrl = await _fileStorageService.UploadFileAsync(processedStream, CampaignImageBucket, processedObject, "image/webp", ct);
            if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return $"s3:{CampaignImageBucket}:{processedObject}";
            return pathOrUrl;
        }

        var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
        var objectName = $"{objectPrefix}{extension}";
        await using var fallback = image.OpenReadStream();
        var uploaded = await _fileStorageService.UploadFileAsync(fallback, CampaignImageBucket, objectName, image.ContentType, ct);
        if (uploaded.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uploaded.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return $"s3:{CampaignImageBucket}:{objectName}";
        return uploaded;
    }

    private async Task<string?> ResolveFileUrlAsync(string? storedPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
            return null;

        if (!storedPath.StartsWith("s3:", StringComparison.OrdinalIgnoreCase))
            return storedPath;

        var parts = storedPath.Split(':', 3, StringSplitOptions.None);
        if (parts.Length < 3)
            return null;

        return await _fileStorageService.GetPresignedUrlAsync(parts[1], parts[2], 7 * 24 * 3600, ct);
    }
}
