using BeeLogistics.Modules.Giveaways.Application.Interfaces;
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
[Route("api/giveaways")]
public class GiveawaysController : BaseController
{
    private const string GiveawayImageBucket = "giveaways";
    private static readonly FileUploadValidationOptions GiveawayImageValidation = FileUploadValidator.ImageOnlyOptions(5_000_000);

    private readonly IGiveawayRepository _repository;
    private readonly IFileStorageService _fileStorageService;
    private readonly IImageProcessor _imageProcessor;
    private readonly IRaffleDrawService _raffleDrawService;

    public GiveawaysController(
        IGiveawayRepository repository,
        IFileStorageService fileStorageService,
        IImageProcessor imageProcessor,
        IRaffleDrawService raffleDrawService)
    {
        _repository = repository;
        _fileStorageService = fileStorageService;
        _imageProcessor = imageProcessor;
        _raffleDrawService = raffleDrawService;
    }

    public class GiveawayUpsertForm
    {
        [FromForm(Name = "title")]
        public string Title { get; set; } = string.Empty;

        [FromForm(Name = "description")]
        public string Description { get; set; } = string.Empty;

        [FromForm(Name = "startDate")]
        public DateTime StartDate { get; set; }

        [FromForm(Name = "endDate")]
        public DateTime EndDate { get; set; }

        [FromForm(Name = "rewardDetails")]
        public string? RewardDetails { get; set; }

        [FromForm(Name = "entryMode")]
        public GiveawayEntryMode EntryMode { get; set; } = GiveawayEntryMode.Manual;

        [FromForm(Name = "maxEntriesPerDriver")]
        public int MaxEntriesPerDriver { get; set; } = 1;

        [FromForm(Name = "dtiPermitNumber")]
        public string? DtiPermitNumber { get; set; }

        [FromForm(Name = "isActive")]
        public bool? IsActive { get; set; }

        [FromForm(Name = "image")]
        public IFormFile? Image { get; set; }

        [FromForm(Name = "dtiPermitImage")]
        public IFormFile? DtiPermitImage { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool activeOnly = false, CancellationToken ct = default)
    {
        var giveaways = await _repository.GetGiveawaysAsync(activeOnly, ct);
        var data = new List<object>(giveaways.Count);

        foreach (var item in giveaways)
        {
            var imageUrl = await ResolveFileUrlAsync(item.ImagePath, ct);
            var dtiImageUrl = await ResolveFileUrlAsync(item.DtiPermitImagePath, ct);
            data.Add(new
            {
                item.Id,
                item.Title,
                item.Description,
                item.StartDate,
                item.EndDate,
                item.RewardDetails,
                item.IsActive,
                ImageUrl = imageUrl,
                item.CreatedAt,
                EntryMode = item.EntryMode.ToString(),
                item.MaxEntriesPerDriver,
                item.DtiPermitNumber,
                DtiPermitImageUrl = dtiImageUrl,
                Status = item.Status.ToString()
            });
        }

        return Ok(new { success = true, data });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
    {
        var giveaway = await _repository.GetGiveawayByIdAsync(id, ct);
        if (giveaway == null)
            return NotFound(new { success = false, message = "Giveaway not found." });

        var imageUrl = await ResolveFileUrlAsync(giveaway.ImagePath, ct);
        var dtiImageUrl = await ResolveFileUrlAsync(giveaway.DtiPermitImagePath, ct);
        return Ok(new
        {
            success = true,
            data = new
            {
                giveaway.Id,
                giveaway.Title,
                giveaway.Description,
                giveaway.StartDate,
                giveaway.EndDate,
                giveaway.RewardDetails,
                giveaway.IsActive,
                ImageUrl = imageUrl,
                giveaway.CreatedAt,
                EntryMode = giveaway.EntryMode.ToString(),
                giveaway.MaxEntriesPerDriver,
                giveaway.DtiPermitNumber,
                DtiPermitImageUrl = dtiImageUrl,
                Status = giveaway.Status.ToString()
            }
        });
    }

    [HttpPost]
    [Authorize(Policy = "Backoffice")]
    [RequestSizeLimit(6_000_000)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Create([FromForm] GiveawayUpsertForm form, CancellationToken ct = default)
    {
        string? imagePath = null;
        if (form.Image != null)
        {
            if (!await FileUploadValidator.ValidateAsync(form.Image, GiveawayImageValidation, ct))
                return BadRequest(new { success = false, message = "Invalid image." });
            imagePath = await UploadImageAsync(form.Image, $"giveaway-{Guid.NewGuid():N}", ct);
        }

        string? dtiImagePath = null;
        if (form.DtiPermitImage != null)
        {
            if (!await FileUploadValidator.ValidateAsync(form.DtiPermitImage, GiveawayImageValidation, ct))
                return BadRequest(new { success = false, message = "Invalid DTI image." });
            dtiImagePath = await UploadImageAsync(form.DtiPermitImage, $"giveaway-dti-{Guid.NewGuid():N}", ct);
        }

        Giveaway giveaway;
        try
        {
            giveaway = new Giveaway(
                form.Title,
                form.Description,
                form.StartDate,
                form.EndDate,
                imagePath,
                form.RewardDetails,
                form.EntryMode,
                form.MaxEntriesPerDriver,
                form.DtiPermitNumber,
                dtiImagePath);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }

        await _repository.AddGiveawayAsync(giveaway, ct);
        return Ok(new { success = true, data = new { giveaway.Id } });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Backoffice")]
    [RequestSizeLimit(6_000_000)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Update(Guid id, [FromForm] GiveawayUpsertForm form, CancellationToken ct = default)
    {
        var giveaway = await _repository.GetGiveawayByIdAsync(id, ct);
        if (giveaway == null)
            return NotFound(new { success = false, message = "Giveaway not found." });

        var imagePath = giveaway.ImagePath;
        if (form.Image != null)
        {
            if (!await FileUploadValidator.ValidateAsync(form.Image, GiveawayImageValidation, ct))
                return BadRequest(new { success = false, message = "Invalid image." });
            imagePath = await UploadImageAsync(form.Image, $"giveaway-{id:N}", ct);
        }

        var dtiImagePath = giveaway.DtiPermitImagePath;
        if (form.DtiPermitImage != null)
        {
            if (!await FileUploadValidator.ValidateAsync(form.DtiPermitImage, GiveawayImageValidation, ct))
                return BadRequest(new { success = false, message = "Invalid DTI image." });
            dtiImagePath = await UploadImageAsync(form.DtiPermitImage, $"giveaway-dti-{id:N}", ct);
        }

        try
        {
            giveaway.Update(
                form.Title,
                form.Description,
                form.StartDate,
                form.EndDate,
                imagePath,
                form.RewardDetails,
                form.IsActive ?? giveaway.IsActive,
                form.EntryMode,
                form.MaxEntriesPerDriver,
                form.DtiPermitNumber,
                dtiImagePath);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }

        await _repository.UpdateGiveawayAsync(giveaway, ct);
        return Ok(new { success = true });
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "Backoffice")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var giveaway = await _repository.GetGiveawayByIdAsync(id, ct);
        if (giveaway == null)
            return NotFound(new { success = false, message = "Giveaway not found." });

        var deletedBy = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _repository.SoftDeleteGiveawayAsync(giveaway, deletedBy, ct);
        return Ok(new { success = true });
    }

    [HttpPost("{id:guid}/enter")]
    [Authorize(Roles = UserRoles.Driver)]
    public async Task<IActionResult> Enter(Guid id, CancellationToken ct = default)
    {
        var giveaway = await _repository.GetGiveawayByIdAsync(id, ct);
        if (giveaway == null)
            return NotFound(new { success = false, message = "Giveaway not found." });

        var now = DateTime.UtcNow;
        if (!giveaway.IsActive || now < giveaway.StartDate || now > giveaway.EndDate)
            return BadRequest(new { success = false, message = "Giveaway is not active." });

        if (giveaway.EntryMode != GiveawayEntryMode.Manual)
            return BadRequest(new { success = false, message = "This giveaway does not accept manual entries." });

        var driverIdRaw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(driverIdRaw, out var driverId))
            return Unauthorized(new { success = false, message = "Invalid driver identity." });

        // Check daily limit (1 per day for manual entry)
        var entriesToday = await _repository.GetEntriesTodayCountAsync(id, driverId, ct);
        if (entriesToday > 0)
            return BadRequest(new { success = false, message = "You have already joined this giveaway today. Come back tomorrow!" });

        // Check total limit
        var totalEntries = await _repository.GetEntryCountAsync(id, driverId, ct);
        if (totalEntries >= giveaway.MaxEntriesPerDriver)
            return BadRequest(new { success = false, message = $"You have reached the maximum allowed entries ({giveaway.MaxEntriesPerDriver}) for this giveaway." });

        var entry = new GiveawayEntry(id, driverId, GiveawayEntrySource.Manual);
        await _repository.AddEntryAsync(entry, ct);
        return Ok(new { success = true, data = new { entry.Id, entry.EnteredAt, EntryCount = totalEntries + 1 } });
    }

    [HttpPost("{id:guid}/prizes")]
    [Authorize(Policy = "Backoffice")]
    public async Task<IActionResult> AddPrize(Guid id, [FromBody] AddPrizeRequest request, CancellationToken ct = default)
    {
        var giveaway = await _repository.GetGiveawayByIdAsync(id, ct);
        if (giveaway == null)
            return NotFound(new { success = false, message = "Giveaway not found." });

        if (giveaway.Status == GiveawayStatus.Drawn || giveaway.Status == GiveawayStatus.Closed)
            return BadRequest(new { success = false, message = "Cannot add prizes to a drawn or closed giveaway." });

        try
        {
            var prize = new GiveawayPrize(id, request.Name, request.Description, request.Quantity, request.Tier);
            await _repository.AddPrizeAsync(prize, ct);
            return Ok(new { success = true, data = new { prize.Id } });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("{id:guid}/prizes")]
    public async Task<IActionResult> GetPrizes(Guid id, CancellationToken ct = default)
    {
        var prizes = await _repository.GetPrizesAsync(id, ct);
        return Ok(new { success = true, data = prizes.Select(p => new { p.Id, p.Name, p.Description, p.Quantity, p.Tier }) });
    }

    [HttpDelete("{id:guid}/prizes/{prizeId:guid}")]
    [Authorize(Policy = "Backoffice")]
    public async Task<IActionResult> DeletePrize(Guid id, Guid prizeId, CancellationToken ct = default)
    {
        var giveaway = await _repository.GetGiveawayByIdAsync(id, ct);
        if (giveaway == null)
            return NotFound(new { success = false, message = "Giveaway not found." });

        if (giveaway.Status == GiveawayStatus.Drawn || giveaway.Status == GiveawayStatus.Closed)
            return BadRequest(new { success = false, message = "Cannot delete prizes from a drawn or closed giveaway." });

        var prize = await _repository.GetPrizeByIdAsync(prizeId, ct);
        if (prize == null || prize.GiveawayId != id)
            return NotFound(new { success = false, message = "Prize not found in this giveaway." });

        await _repository.DeletePrizeAsync(prize, ct);
        return Ok(new { success = true });
    }

    [HttpPost("{id:guid}/draw")]
    [Authorize(Policy = "Backoffice")]
    public async Task<IActionResult> DrawWinners(Guid id, CancellationToken ct = default)
    {
        var result = await _raffleDrawService.DrawWinnersAsync(id, ct);
        if (!result.IsSuccess)
            return BadRequest(new { success = false, message = result.Error });

        return Ok(new { success = true, data = result.Value.Select(w => new { w.Id, w.DriverId, w.GiveawayPrizeId, w.DrawnAt }) });
    }

    [HttpGet("{id:guid}/winners")]
    public async Task<IActionResult> GetWinners(Guid id, CancellationToken ct = default)
    {
        var winners = await _repository.GetWinnersAsync(id, ct);
        return Ok(new { success = true, data = winners.Select(w => new {
            w.Id,
            w.DriverId,
            PrizeId = w.GiveawayPrizeId,
            w.Prize.Name,
            w.Prize.Tier,
            w.DrawnAt
        })});
    }

    [HttpGet("{id:guid}/entries")]
    [Authorize(Policy = "Backoffice")]
    public async Task<IActionResult> GetEntries(Guid id, CancellationToken ct = default)
    {
        var entries = await _repository.GetEntriesForGiveawayAsync(id, ct);
        return Ok(new { success = true, data = entries.Select(e => new { e.Id, e.DriverId, Source = e.Source.ToString(), e.BookingId, e.EnteredAt }) });
    }

    public class AddPrizeRequest
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int Quantity { get; set; }
        public int Tier { get; set; }
    }

    private async Task<string> UploadImageAsync(IFormFile image, string objectPrefix, CancellationToken ct)
    {
        var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
        var objectName = $"{objectPrefix}{extension}";

        await using var inputStream = image.OpenReadStream();
        await using var processedStream = new MemoryStream();
        var processed = await _imageProcessor.ProcessImageAsync(inputStream, processedStream, ct);

        if (processed.Success)
        {
            processedStream.Position = 0;
            var processedObject = $"{objectPrefix}.webp";
            var pathOrUrl = await _fileStorageService.UploadFileAsync(processedStream, GiveawayImageBucket, processedObject, "image/webp", ct);
            if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return $"s3:{GiveawayImageBucket}:{processedObject}";
            return pathOrUrl;
        }

        await using var fallback = image.OpenReadStream();
        var contentType = image.ContentType;
        var uploaded = await _fileStorageService.UploadFileAsync(fallback, GiveawayImageBucket, objectName, contentType, ct);
        if (uploaded.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uploaded.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return $"s3:{GiveawayImageBucket}:{objectName}";
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
