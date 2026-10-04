using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Infrastructure;
using BeeLogistics.Shared.Presentation;
using MassTransit;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Security.Cryptography;

namespace BeeLogistics.Modules.Drivers.Presentation.Controllers;

[Route("api/driver-applications")]
public class DriverApplicationsController : BaseController
{
    private readonly IDriverApplicationRepository _driverApplicationRepository;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IFileStorageService _fileStorageService;
    private readonly IImageProcessor _imageProcessor;
    private readonly BeeLogistics.Modules.Notification.Application.Interfaces.IEmailService _emailService;
    private readonly BeeLogistics.Modules.Notification.Application.Services.IEmailTemplateService _emailTemplateService;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly IPushDispatcher _pushDispatcher;
    private readonly ILogger<DriverApplicationsController> _logger;
    private readonly IMediator _mediator;
    private readonly BeeLogistics.Modules.Verification.Application.Interfaces.IDriverVerificationLookup? _verificationLookup;
    private readonly BeeLogistics.Modules.Verification.Application.Interfaces.IDriverIdDocumentProvider? _idDocumentProvider;

    public DriverApplicationsController(
        IDriverApplicationRepository driverApplicationRepository,
        UserManager<ApplicationUser> userManager,
        IFileStorageService fileStorageService,
        IImageProcessor imageProcessor,
        BeeLogistics.Modules.Notification.Application.Interfaces.IEmailService emailService,
        BeeLogistics.Modules.Notification.Application.Services.IEmailTemplateService emailTemplateService,
        IPublishEndpoint publishEndpoint,
        IPushDispatcher pushDispatcher,
        ILogger<DriverApplicationsController> logger,
        IMediator mediator,
        BeeLogistics.Modules.Verification.Application.Interfaces.IDriverVerificationLookup? verificationLookup = null,
        BeeLogistics.Modules.Verification.Application.Interfaces.IDriverIdDocumentProvider? idDocumentProvider = null)
    {
        _driverApplicationRepository = driverApplicationRepository;
        _userManager = userManager;
        _fileStorageService = fileStorageService;
        _imageProcessor = imageProcessor;
        _emailService = emailService;
        _emailTemplateService = emailTemplateService;
        _publishEndpoint = publishEndpoint;
        _pushDispatcher = pushDispatcher;
        _logger = logger;
        _mediator = mediator;
        _verificationLookup = verificationLookup;
        _idDocumentProvider = idDocumentProvider;
    }

    /// <summary>
    /// Publishes a back-office event without letting a broker outage break
    /// the user-facing request. Delivery reliability past this point is
    /// MassTransit's job (outbox + redelivery).
    /// </summary>
    private async Task PublishBackofficeEventAsync<T>(T @event, CancellationToken ct) where T : class
    {
        try
        {
            await _publishEndpoint.Publish(@event, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish {EventType} for back-office webhook", typeof(T).Name);
        }
    }

    private string GetDriverApprovedEmailBody(string fullName, string password)
    {
        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <title>Driver Application Approved</title>
</head>
<body style=""font-family: sans-serif; line-height: 1.6; color: #333;"">
    <div style=""max-width: 600px; margin: 0 auto; padding: 20px;"">
        <h1 style=""color: #28a745;"">Application Approved!</h1>
        <p>Dear {fullName},</p>
        <p>Congratulations! Your driver application for My Bee App has been approved.</p>
        <p>An account has been created for you. You can log in using your email address and the following temporary password:</p>
        <div style=""background-color: #f8f9fa; padding: 15px; border-radius: 5px; text-align: center; margin: 20px 0;"">
            <strong style=""font-size: 24px; letter-spacing: 2px;"">{password}</strong>
        </div>
        <p><strong>Please change your password immediately after logging in.</strong></p>
        <p>Welcome to the team!</p>
        <p>Best regards,<br>The BeeLogistics Team</p>
    </div>
</body>
</html>";
    }

    public class CreateDriverApplicationForm
    {
        [FromForm(Name = "fullName")]
        public string FullName { get; set; } = string.Empty;

        [FromForm(Name = "phone")]
        public string Phone { get; set; } = string.Empty;

        [FromForm(Name = "facebookProfileUrl")]
        public string? FacebookProfileUrl { get; set; }

        [FromForm(Name = "vehicleType")]
        public string? VehicleType { get; set; }

        [FromForm(Name = "vehiclePlate")]
        public string? VehiclePlate { get; set; }

        [FromForm(Name = "vehicleModel")]
        public string? VehicleModel { get; set; }

        [FromForm(Name = "vehicleColor")]
        public string? VehicleColor { get; set; }

        [FromForm(Name = "clearance")]
        public IFormFile? Clearance { get; set; }

        [FromForm(Name = "orCr")]
        public IFormFile? OrCr { get; set; }

        [FromForm(Name = "ltfrbPa")]
        public IFormFile? LtfrbPa { get; set; }

        [FromForm(Name = "insurance")]
        public IFormFile? Insurance { get; set; }
    }

    private const string DriverDocumentsBucket = "driver-applications";

    // Total submissions allowed per application (initial submit + resubmissions after rejection).
    private const int MaxSubmissionCount = 5;

    private static readonly FileUploadValidationOptions DriverDocumentValidationOptions = FileUploadValidator.ImageAndPdfOptions();

    /// <summary>
    /// Masks email address for privacy: u***@example.com
    /// </summary>
    private static string MaskEmail(string email)
    {
        if (string.IsNullOrEmpty(email)) return string.Empty;
        
        var parts = email.Split('@');
        if (parts.Length != 2) return email;
        
        var username = parts[0];
        var domain = parts[1];
        
        if (username.Length <= 1)
            return $"{username[0]}***@{domain}";
        
        return $"{username[0]}***@{domain}";
    }

    /// <summary>
    /// Masks phone number for privacy: +63***1234
    /// </summary>
    private static string MaskPhone(string phone)
    {
        if (string.IsNullOrEmpty(phone)) return string.Empty;
        
        // Keep country code and last 4 digits, mask the rest
        if (phone.Length <= 4)
            return "***" + phone.Substring(Math.Max(0, phone.Length - 4));
        
        var last4 = phone.Substring(phone.Length - 4);
        var prefix = phone.Substring(0, Math.Min(3, phone.Length - 4));
        
        return $"{prefix}***{last4}";
    }

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png"];

    /// <summary>
    /// Uploads a driver document via IFileStorageService (S3/R2 or local per config).
    /// Images (jpg/png) are converted to WebP; PDFs are stored as-is.
    /// Returns a stored reference: for S3 we store "s3:bucket:objectKey" so GetDocument can presign; for local we store the path.
    /// </summary>
    private async Task<string> UploadDocumentAsync(IFormFile file, Guid applicationId, string name, CancellationToken ct)
    {
        var originalFileName = Path.GetFileName(file.FileName);
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (!DriverDocumentValidationOptions.AllowedExtensions.Contains(extension))
            throw new InvalidOperationException($"Invalid file extension: {extension}");

        if (extension == ".pdf")
        {
            var objectName = $"{applicationId:N}/{name}{extension}";
            var contentType = "application/pdf";
            await using var stream = file.OpenReadStream();
            var pathOrUrl = await _fileStorageService.UploadFileAsync(stream, DriverDocumentsBucket, objectName, contentType, ct);
            if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return $"s3:{DriverDocumentsBucket}:{objectName}";
            return pathOrUrl;
        }

        if (ImageExtensions.Contains(extension))
        {
            await using var inputStream = file.OpenReadStream();
            return await UploadImageStreamAsync(inputStream, applicationId, name, ct);
        }

        var fallbackObjectName = $"{applicationId:N}/{name}{extension}";
        var fallbackContentType = file.ContentType ?? (extension switch { ".pdf" => "application/pdf", ".png" => "image/png", _ => "image/jpeg" });
        await using var fallbackStream = file.OpenReadStream();
        var fallbackPathOrUrl = await _fileStorageService.UploadFileAsync(fallbackStream, DriverDocumentsBucket, fallbackObjectName, fallbackContentType, ct);
        if (fallbackPathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || fallbackPathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return $"s3:{DriverDocumentsBucket}:{fallbackObjectName}";
        return fallbackPathOrUrl;
    }

    /// <summary>
    /// Shared image-upload path used both for driver-uploaded IFormFile images and for the
    /// Didit-sourced ID document image: converts to WebP and stores under the driver-applications
    /// bucket, same "s3:bucket:objectKey" convention as UploadDocumentAsync.
    /// </summary>
    private async Task<string> UploadImageStreamAsync(Stream inputStream, Guid applicationId, string name, CancellationToken ct)
    {
        await using var processedStream = new MemoryStream();
        var processingResult = await _imageProcessor.ProcessImageAsync(inputStream, processedStream, ct);
        if (!processingResult.Success)
            throw new InvalidOperationException($"Image processing failed: {processingResult.ErrorMessage ?? "Unknown error"}");

        processedStream.Position = 0;
        var objectName = $"{applicationId:N}/{name}.webp";
        var contentType = "image/webp";
        var pathOrUrl = await _fileStorageService.UploadFileAsync(processedStream, DriverDocumentsBucket, objectName, contentType, ct);
        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return $"s3:{DriverDocumentsBucket}:{objectName}";
        return pathOrUrl;
    }

    /// <summary>
    /// Fetches the Didit-extracted ID document portrait for an Approved driver and stores it
    /// as the license image, replacing the old manual driversLicense upload. Throws if KYC
    /// isn't Approved or Didit has no image available — the caller must not persist an empty path.
    /// </summary>
    private async Task<string> UploadIdDocumentFromDiditAsync(string userId, Guid applicationId, CancellationToken ct)
    {
        if (_idDocumentProvider == null)
            throw new InvalidOperationException("Identity verification image lookup is unavailable. Please try again shortly.");

        var image = await _idDocumentProvider.GetLatestAvailableIdImageAsync(userId, ct);
        if (image == null)
            throw new InvalidOperationException("Your verified ID image is not available yet. Please try again shortly.");

        await using var stream = image.Content;
        return await UploadImageStreamAsync(stream, applicationId, "drivers-license", ct);
    }

    [HttpPost]
    [Authorize(Roles = UserRoles.Driver)]
    [RequestSizeLimit(42_500_000)] // 42.5MB max (4 files × 10MB + 2.5MB form overhead) - drivers-license is now sourced from Didit, not uploaded
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Create([FromForm] CreateDriverApplicationForm form, CancellationToken ct)
    {
        // Get authenticated user
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(new { success = false, message = "User not authenticated" });
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return Unauthorized(new { success = false, message = "User not found" });
        }

        // SECURITY: Verify user is a Driver
        if (user.Role != UserRoles.Driver)
        {
            return Unauthorized(new { success = false, message = "Only drivers can submit driver applications" });
        }

        // SECURITY: Verify at least one contact method is verified (email OR phone)
        if (!user.EmailConfirmed && !user.PhoneNumberConfirmed)
        {
            return BadRequest(new { success = false, message = "Email or phone number must be verified before submitting driver application" });
        }

        // Get email from authenticated user (not from form for security)
        var email = user.Email;
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest(new { success = false, message = "User email not found" });
        }

        // SECURITY: The license image is now sourced from Didit's own ID-document scan rather
        // than a manual upload, so it must have run at least far enough to produce one.
        // "InReview" (Didit's own manual-check state) still counts: the ID document has already
        // been scanned by that point, and the whole reason to collect it here is so our own
        // back-office can review it — it doesn't have to wait on Didit's final decision.
        var verificationSummary = _verificationLookup != null
            ? await _verificationLookup.GetLatestForUserAsync(userId, ct)
            : null;
        if (verificationSummary?.Status is not ("Approved" or "InReview"))
        {
            return BadRequest(new { success = false, message = "Please complete identity verification before submitting your driver application." });
        }

        // Check if application already exists for this user (by UserId, fallback to Email for backward compatibility).
        // Pending/Approved applications block a new submit; Rejected ones may be re-submitted (up to the cap).
        var existingApplication = await _driverApplicationRepository.GetByUserIdOrEmailAsync(userId, email, ct);
        var isResubmission = false;

        if (existingApplication != null)
        {
            switch (existingApplication.Status)
            {
                case DriverApplicationStatus.Pending:
                    return BadRequest(new
                    {
                        success = false,
                        message = "Your driver application is still pending review. Please wait for the review result before submitting again.",
                        existingApplicationId = existingApplication.Id,
                        status = existingApplication.Status.ToString()
                    });

                case DriverApplicationStatus.Approved:
                    return BadRequest(new
                    {
                        success = false,
                        message = "Your driver application has already been approved. Contact support if you need to update your details.",
                        existingApplicationId = existingApplication.Id,
                        status = existingApplication.Status.ToString()
                    });

                case DriverApplicationStatus.Rejected when existingApplication.SubmissionCount >= MaxSubmissionCount:
                    return BadRequest(new
                    {
                        success = false,
                        message = $"You have reached the maximum of {MaxSubmissionCount} driver application submissions. Please contact support for assistance.",
                        existingApplicationId = existingApplication.Id,
                        status = existingApplication.Status.ToString()
                    });

                default:
                    // Rejected and under the cap: create a brand-new application
                    // (new Id) so the driver can have multiple applications on
                    // record until they are onboarded (approved).
                    isResubmission = true;
                    break;
            }
        }

        // Validate required fields
        if (string.IsNullOrWhiteSpace(form.FullName) ||
            string.IsNullOrWhiteSpace(form.Phone))
        {
            return BadRequest(new { success = false, message = "Full name and phone number are required." });
        }

        if (string.IsNullOrWhiteSpace(form.VehicleType))
        {
            return BadRequest(new { success = false, message = "Vehicle type is required." });
        }

        // The vehicle pricing table is the single source of truth for vehicle types. It is
        // owned by the Bookings module, so it is read through a Shared.Contracts query
        // rather than by referencing that module's repository.
        IReadOnlyList<string> activeVehicleTypes;
        try
        {
            var vehicleTypesResult = await _mediator.Send(new GetActiveVehicleTypesQuery(), ct);
            if (!vehicleTypesResult.IsSuccess || vehicleTypesResult.Value == null)
            {
                _logger.LogError("Vehicle type catalog query failed: {Error}", vehicleTypesResult.Error);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { success = false, message = "Vehicle types are temporarily unavailable. Please try again in a moment." });
            }
            activeVehicleTypes = vehicleTypesResult.Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vehicle type catalog query threw while validating a driver application");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { success = false, message = "Vehicle types are temporarily unavailable. Please try again in a moment." });
        }

        // Fail closed on an empty catalog: approving a driver into a class with no pricing
        // row leaves them permanently unmatchable, which is worse than a retryable failure.
        if (activeVehicleTypes.Count == 0)
        {
            _logger.LogError("Vehicle type catalog is empty — cannot validate driver application vehicle type");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { success = false, message = "Vehicle types are temporarily unavailable. Please try again in a moment." });
        }

        var canonicalVehicleType = VehicleTypeResolver.ResolveCanonical(activeVehicleTypes, form.VehicleType);
        if (canonicalVehicleType == null)
        {
            return BadRequest(new { success = false, message = $"Vehicle type is required. Allowed values: {string.Join(", ", activeVehicleTypes)}." });
        }

        // Validate at least one of the remaining (manually-uploaded) documents is provided.
        // The drivers-license image is no longer user-supplied — it's sourced from Didit below.
        // On resubmission, documents that are not re-uploaded keep their previously
        // uploaded files, so retained existing paths also satisfy this rule.
        var hasAnyDocument = form.Clearance != null ||
                             form.OrCr != null ||
                             form.LtfrbPa != null ||
                             form.Insurance != null;

        if (isResubmission && !hasAnyDocument)
        {
            hasAnyDocument = !string.IsNullOrWhiteSpace(existingApplication!.ClearancePath) ||
                             !string.IsNullOrWhiteSpace(existingApplication.OrCrPath) ||
                             !string.IsNullOrWhiteSpace(existingApplication.LtfrbPaPath) ||
                             !string.IsNullOrWhiteSpace(existingApplication.InsurancePath);
        }

        if (!hasAnyDocument)
        {
            return BadRequest(new { success = false, message = "At least one document must be provided." });
        }

        // SECURITY: Comprehensive file validation - extension, Content-Type, and actual file content (magic bytes)
        // Only validate files that are provided (optional fields)
        if (form.Clearance != null && !await FileUploadValidator.ValidateAsync(form.Clearance, DriverDocumentValidationOptions, ct))
        {
            return BadRequest(new { success = false, message = "Clearance file is invalid. Only PDF and JPG/PNG image files are allowed." });
        }
        if (form.OrCr != null && !await FileUploadValidator.ValidateAsync(form.OrCr, DriverDocumentValidationOptions, ct))
        {
            return BadRequest(new { success = false, message = "OR/CR file is invalid. Only PDF and JPG/PNG image files are allowed." });
        }
        if (form.LtfrbPa != null && !await FileUploadValidator.ValidateAsync(form.LtfrbPa, DriverDocumentValidationOptions, ct))
        {
            return BadRequest(new { success = false, message = "LTFRB PA file is invalid. Only PDF and JPG/PNG image files are allowed." });
        }
        if (form.Insurance != null && !await FileUploadValidator.ValidateAsync(form.Insurance, DriverDocumentValidationOptions, ct))
        {
            return BadRequest(new { success = false, message = "Insurance file is invalid. Only PDF and JPG/PNG image files are allowed." });
        }

        // Sanitize inputs
        var sanitizedFullName = InputSanitizer.SanitizePlainText(form.FullName);
        var sanitizedPhone = InputSanitizer.SanitizePlainText(form.Phone);
        var sanitizedFacebookUrl = InputSanitizer.SanitizeUrl(form.FacebookProfileUrl);

        // Canonical casing comes from the matched pricing row, resolved during validation above.
        var vehicleType = canonicalVehicleType;
        var vehiclePlate = string.IsNullOrWhiteSpace(form.VehiclePlate) ? null : InputSanitizer.SanitizePlainText(form.VehiclePlate);
        var vehicleModel = string.IsNullOrWhiteSpace(form.VehicleModel) ? null : InputSanitizer.SanitizePlainText(form.VehicleModel);
        var vehicleColor = string.IsNullOrWhiteSpace(form.VehicleColor) ? null : InputSanitizer.SanitizePlainText(form.VehicleColor);

        if (isResubmission)
        {
            var previous = existingApplication!;
            var newSubmissionCount = previous.SubmissionCount + 1;

            // A resubmission is a brand-new application row. Freshly uploaded
            // documents are namespaced under the new application's Id; documents
            // that are not re-uploaded carry their previous stored paths forward.
            var resubmission = new DriverApplication(
                sanitizedFullName,
                email, // Use email from authenticated user (identity-bound)
                sanitizedPhone,
                sanitizedFacebookUrl,
                driversLicensePath: previous.DriversLicensePath,
                clearancePath: previous.ClearancePath,
                orCrPath: previous.OrCrPath,
                ltfrbPaPath: previous.LtfrbPaPath,
                insurancePath: previous.InsurancePath,
                userId: userId,
                vehicleType: vehicleType,
                vehiclePlate: vehiclePlate,
                vehicleModel: vehicleModel,
                vehicleColor: vehicleColor,
                submissionCount: newSubmissionCount);

            string newDriversLicensePath;
            try
            {
                newDriversLicensePath = await UploadIdDocumentFromDiditAsync(userId, resubmission.Id, ct);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            var newClearancePath = form.Clearance != null
                ? await UploadDocumentAsync(form.Clearance, resubmission.Id, "clearance", ct)
                : previous.ClearancePath;
            var newOrCrPath = form.OrCr != null
                ? await UploadDocumentAsync(form.OrCr, resubmission.Id, "orcr", ct)
                : previous.OrCrPath;
            var newLtfrbPaPath = form.LtfrbPa != null
                ? await UploadDocumentAsync(form.LtfrbPa, resubmission.Id, "ltfrb-pa", ct)
                : previous.LtfrbPaPath;
            var newInsurancePath = form.Insurance != null
                ? await UploadDocumentAsync(form.Insurance, resubmission.Id, "insurance", ct)
                : previous.InsurancePath;

            resubmission.SetDocuments(
                newDriversLicensePath,
                newClearancePath,
                newOrCrPath,
                newLtfrbPaPath,
                newInsurancePath);

            _driverApplicationRepository.Add(resubmission);
            await _driverApplicationRepository.SaveChangesAsync(ct);

            // Notify the back-office backend, exactly like a fresh submit. The new
            // Id creates a new review record so every attempt is kept on record.
            await PublishBackofficeEventAsync(new DriverApplicationSubmittedEvent(
                resubmission.Id, userId, sanitizedFullName, DateTime.UtcNow), ct);

            return Ok(new
            {
                success = true,
                data = new
                {
                    resubmission.Id,
                    resubmission.FullName,
                    resubmission.Email,
                    resubmission.Phone,
                    resubmission.VehicleType,
                    resubmission.VehiclePlate,
                    resubmission.Status,
                    resubmission.SubmissionCount
                }
            });
        }

        var tempApp = new DriverApplication(
            sanitizedFullName,
            email, // Use email from authenticated user
            sanitizedPhone,
            sanitizedFacebookUrl,
            driversLicensePath: string.Empty,
            clearancePath: string.Empty,
            orCrPath: string.Empty,
            ltfrbPaPath: string.Empty,
            insurancePath: string.Empty);

        // Save files via IFileStorageService (S3/R2 or local per FileStorage:UseLocal)
        string driversLicensePath;
        try
        {
            driversLicensePath = await UploadIdDocumentFromDiditAsync(userId, tempApp.Id, ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        var clearancePath = form.Clearance != null
            ? await UploadDocumentAsync(form.Clearance, tempApp.Id, "clearance", ct)
            : string.Empty;
        var orCrPath = form.OrCr != null
            ? await UploadDocumentAsync(form.OrCr, tempApp.Id, "orcr", ct)
            : string.Empty;
        var ltfrbPaPath = form.LtfrbPa != null
            ? await UploadDocumentAsync(form.LtfrbPa, tempApp.Id, "ltfrb-pa", ct)
            : string.Empty;
        var insurancePath = form.Insurance != null
            ? await UploadDocumentAsync(form.Insurance, tempApp.Id, "insurance", ct)
            : string.Empty;

        var application = new DriverApplication(
            sanitizedFullName,
            email, // Use email from authenticated user
            sanitizedPhone,
            sanitizedFacebookUrl,
            driversLicensePath,
            clearancePath,
            orCrPath,
            ltfrbPaPath,
            insurancePath,
            userId: userId, // Set UserId FK
            vehicleType: vehicleType,
            vehiclePlate: vehiclePlate,
            vehicleModel: vehicleModel,
            vehicleColor: vehicleColor);

        _driverApplicationRepository.Add(application);
        await _driverApplicationRepository.SaveChangesAsync(ct);

        // Notify the back-office backend (webhook via MassTransit consumer).
        await PublishBackofficeEventAsync(new DriverApplicationSubmittedEvent(
            application.Id, userId, sanitizedFullName, DateTime.UtcNow), ct);

        return Ok(new
        {
            success = true,
            data = new
            {
                application.Id,
                application.FullName,
                application.Email,
                application.Phone,
                application.VehicleType,
                application.VehiclePlate,
                application.Status,
                application.SubmissionCount
            }
        });
    }

    /// <summary>
    /// Get the current user's driver application (if any).
    /// SECURITY: Scoped by authenticated UserId only — returns only the application for the logged-in user.
    /// Uses UserId from claims; fallback to Email for applications created before UserId was set.
    /// </summary>
    [HttpGet("my-application")]
    [Authorize(Roles = UserRoles.Driver)]
    public async Task<IActionResult> GetMyApplication(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(new { success = false, message = "User not authenticated" });
        }

        // Primary: find by UserId (FK to ApplicationUser)
        var application = await _driverApplicationRepository.GetByUserIdAsync(userId, ct);

        // Fallback: applications created before UserId was set may only have Email
        if (application == null)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (!string.IsNullOrEmpty(user?.Email))
            {
                application = await _driverApplicationRepository.GetByEmailAsync(user.Email, ct);
            }
        }

        if (application == null)
        {
            return NotFound(new { success = false, message = "No application found" });
        }

        return Ok(new
        {
            success = true,
            data = new
            {
                application.Id,
                application.FullName,
                application.Email,
                application.Phone,
                application.VehicleType,
                application.VehiclePlate,
                application.Status,
                application.Notes, // Review notes (e.g. rejection reason) so the driver app can show it
                application.SubmissionCount,
                application.CreatedAt
            }
        });
    }

    [HttpGet]
    [Authorize(Policy = "BackofficeDriverApplications")]
    public async Task<IActionResult> GetAll([FromQuery] DriverApplicationStatus? status = null, CancellationToken ct = default)
    {
        var list = await _driverApplicationRepository.GetAllByStatusAsync(status, ct);

        // KYC (Didit) verification state per applicant, so admins see identity
        // verification results next to the documents when reviewing.
        IReadOnlyDictionary<string, BeeLogistics.Modules.Verification.Application.Interfaces.DriverVerificationSummary> kycByUser =
            new Dictionary<string, BeeLogistics.Modules.Verification.Application.Interfaces.DriverVerificationSummary>();
        if (_verificationLookup != null)
        {
            var userIds = list.Where(x => !string.IsNullOrEmpty(x.UserId)).Select(x => x.UserId!).Distinct().ToArray();
            try
            {
                kycByUser = await _verificationLookup.GetLatestForUsersAsync(userIds, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load KYC summaries for driver applications list");
            }
        }

        // SECURITY: Mask sensitive PII data (email, phone, Facebook URL)
        var items = list.Select(x => new
        {
            x.Id,
            x.FullName,
            Email = MaskEmail(x.Email), // Mask email: u***@example.com
            Phone = MaskPhone(x.Phone), // Mask phone: +63***1234
            FacebookProfileUrl = !string.IsNullOrEmpty(x.FacebookProfileUrl) ? "[Hidden]" : null,
            x.VehicleType,
            x.VehiclePlate,
            x.VehicleModel,
            x.VehicleColor,
            x.Status,
            x.CreatedAt,
            Documents = new
            {
                x.DriversLicensePath,
                x.ClearancePath,
                x.OrCrPath,
                x.LtfrbPaPath,
                x.InsurancePath
            },
            Kyc = x.UserId != null && kycByUser.TryGetValue(x.UserId, out var kyc) ? kyc : null
        }).ToList();

        return Ok(new { success = true, data = items });
    }

    public class ReviewRequest
    {
        public string? Notes { get; set; }
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = "BackofficeDriverApplications")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] ReviewRequest request, CancellationToken ct)
    {
        var application = await _driverApplicationRepository.GetByIdAsync(id, ct);
        if (application == null)
            return NotFound(new { success = false, message = "Application not found" });

        if (application.Status != DriverApplicationStatus.Pending)
            return BadRequest(new { success = false, message = "Application is not pending" });

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "system";
        application.Approve(currentUserId, request.Notes);
        _driverApplicationRepository.Update(application);
        await _driverApplicationRepository.SaveChangesAsync(ct);

        // Reviewer for the audit trail: the human admin behind an S2S call
        // (acting_admin claim) beats the service/user principal id.
        var approveReviewer = User.FindFirstValue("acting_admin") ?? currentUserId;
        await PublishBackofficeEventAsync(new DriverApplicationReviewedEvent(
            application.Id, "Approved", approveReviewer, request.Notes, DateTime.UtcNow), ct);

        // Find existing user account (should exist since they registered and verified email)
        var existingUser = await _userManager.FindByEmailAsync(application.Email);
        bool accountUpdated = false;

        if (existingUser != null)
        {
            // User account already exists (from registration flow)
            // Update user to mark as onboarded and record the approved vehicle
            existingUser.IsOnboarded = true;
            if (!string.IsNullOrEmpty(application.VehicleType)) existingUser.VehicleType = application.VehicleType;
            if (!string.IsNullOrEmpty(application.VehiclePlate)) existingUser.VehiclePlate = application.VehiclePlate;
            if (!string.IsNullOrEmpty(application.VehicleModel)) existingUser.VehicleModel = application.VehicleModel;
            if (!string.IsNullOrEmpty(application.VehicleColor)) existingUser.VehicleColor = application.VehicleColor;
            var updateResult = await _userManager.UpdateAsync(existingUser);
            if (updateResult.Succeeded)
            {
                accountUpdated = true;
            }

            // Notify the driver that their application was approved.
            // Email failures must never fail the approval request.
            try
            {
                var approvedEmail = _emailTemplateService.CreateDriverApplicationApprovedEmail(
                    application.Email, application.FullName);
                await _emailService.SendAsync(approvedEmail, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send driver application approved email for application {ApplicationId}", application.Id);
            }

            // Push the driver too (in-app + when the app is closed). Push failures must never
            // fail the approval; a freshly-approved driver may have no device yet (DevicesSent 0).
            await TryPushDriverAsync(existingUser.Id, "Application approved 🎉",
                "Your driver application has been approved. You can now start accepting bookings.",
                "approved", application.Id, ct);
        }
        else
        {
            // Legacy flow: Create driver user account if it doesn't exist yet
            // This shouldn't happen in normal flow, but kept for backward compatibility
            var generatedPassword = GenerateSecurePassword(12);

            var user = new ApplicationUser
            {
                UserName = application.Email,
                Email = application.Email,
                FullName = application.FullName,
                Role = UserRoles.Driver,
                IsActive = true,
                IsOnboarded = true, // Mark as onboarded when creating from approval
                EmailConfirmed = true, // Assume email is verified if creating from approval
                VehicleType = application.VehicleType,
                VehiclePlate = application.VehiclePlate,
                VehicleModel = application.VehicleModel,
                VehicleColor = application.VehicleColor
            };

            var result = await _userManager.CreateAsync(user, generatedPassword);
            if (result.Succeeded)
            {
                await _userManager.AddToRoleAsync(user, UserRoles.Driver);
                accountUpdated = true;
                
                // SECURITY: Send password via email instead of returning in response
                try 
                {
                    var emailBody = GetDriverApprovedEmailBody(application.FullName, generatedPassword);
                    await _emailService.SendAsync(new BeeLogistics.Modules.Notification.Application.Interfaces.EmailMessage(
                        To: application.Email,
                        Subject: "Driver Application Approved - My Bee App",
                        Body: emailBody,
                        IsHtml: true
                    ), ct);
                }
                catch (Exception ex)
                {
                    // Log error but don't fail the request, admin might need to reset password manually if email fails
                    // In a real scenario, we might want to return a warning
                    _logger.LogError(ex, "Failed to send driver approval email with credentials for application {ApplicationId}", application.Id);
                }

                await TryPushDriverAsync(user.Id, "Application approved 🎉",
                    "Your driver application has been approved. Check your email for login details.",
                    "approved", application.Id, ct);
            }
        }

        return Ok(new
        {
            success = true,
            data = new
            {
                application.Id,
                application.Status,
                accountUpdated,
                message = existingUser != null
                    ? "Driver application approved. User account has been updated."
                    : "Driver application approved. User account created successfully. Credentials sent to email."
            }
        });
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = "BackofficeDriverApplications")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] ReviewRequest request, CancellationToken ct)
    {
        var application = await _driverApplicationRepository.GetByIdAsync(id, ct);
        if (application == null)
            return NotFound(new { success = false, message = "Application not found" });

        if (application.Status != DriverApplicationStatus.Pending)
            return BadRequest(new { success = false, message = "Application is not pending" });

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "system";
        application.Reject(currentUserId, request.Notes);
        _driverApplicationRepository.Update(application);
        await _driverApplicationRepository.SaveChangesAsync(ct);

        var rejectReviewer = User.FindFirstValue("acting_admin") ?? currentUserId;
        await PublishBackofficeEventAsync(new DriverApplicationReviewedEvent(
            application.Id, "Rejected", rejectReviewer, request.Notes, DateTime.UtcNow), ct);

        // Notify the applicant that their application was rejected.
        // Email failures must never fail the rejection request.
        try
        {
            var rejectedEmail = _emailTemplateService.CreateDriverApplicationRejectedEmail(
                application.Email, application.FullName, request.Notes);
            await _emailService.SendAsync(rejectedEmail, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send driver application rejected email for application {ApplicationId}", application.Id);
        }

        // Push the applicant too. UserId is nullable for legacy apps — skip push when absent.
        var rejectBody = string.IsNullOrWhiteSpace(request.Notes)
            ? "Your driver application was not approved. Contact support for details."
            : $"Your driver application was not approved. Reason: {request.Notes}";
        await TryPushDriverAsync(application.UserId, "Application update",
            rejectBody, "rejected", application.Id, ct);

        return Ok(new { success = true });
    }

    /// <summary>
    /// Fire-and-forget push to a driver about their application outcome. Never throws — a push
    /// failure (or a missing UserId on a legacy application) must not fail the review request.
    /// The push is queued onto the SendPush bus and recorded in the notification history.
    /// </summary>
    private async Task TryPushDriverAsync(string? userId, string title, string body, string status, Guid applicationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogInformation("Skipping driver application {Status} push for application {ApplicationId}: no UserId.", status, applicationId);
            return;
        }

        try
        {
            _logger.LogInformation(
                "[NOTIF-TRACE] Driver application {Status} push -> IPushDispatcher (userId={UserId}, applicationId={ApplicationId})",
                status, userId, applicationId);

            await _pushDispatcher.DispatchAsync(new SendPushRequested
            {
                UserId = userId,
                AppType = "driver",
                Title = title,
                Body = body,
                Data = new Dictionary<string, string>
                {
                    ["type"] = "driver_application",
                    ["status"] = status,
                    ["applicationId"] = applicationId.ToString()
                }
            }, "driver-approval", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue driver application {Status} push for application {ApplicationId}", status, applicationId);
        }
    }

    [HttpGet("{id:guid}/document/{type}")]
    [Authorize(Policy = "BackofficeDriverApplications")]
    public async Task<IActionResult> GetDocument(Guid id, string type, CancellationToken ct)
    {
        var application = await _driverApplicationRepository.GetByIdAsync(id, ct);
        if (application == null)
            return NotFound(new { success = false, message = "Application not found" });

        string? relativePath = type.ToLowerInvariant() switch
        {
            "license" or "driverslicense" => application.DriversLicensePath,
            "clearance" => application.ClearancePath,
            "orcr" => application.OrCrPath,
            "ltfrbpa" or "ltfrb" => application.LtfrbPaPath,
            "insurance" => application.InsurancePath,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(relativePath))
            return NotFound(new { success = false, message = "Document not found" });

        // Stored value is either "s3:bucket:objectKey" (S3/R2) or a local relative path
        if (relativePath.StartsWith("s3:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = relativePath.Split(':', 3, StringSplitOptions.None);
            if (parts.Length >= 3)
            {
                var bucket = parts[1];
                var objectKey = parts[2];
                var presignedUrl = await _fileStorageService.GetPresignedUrlAsync(bucket, objectKey, expiresInSeconds: 3600, ct);
                return Redirect(presignedUrl);
            }
            return NotFound(new { success = false, message = "Invalid document reference" });
        }

        // Local path: IFileStorageService (local) uses wwwroot/uploads; legacy used App_Data/uploads/drivers
        var normalizedPath = relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()).TrimStart(Path.DirectorySeparatorChar);
        var wwwrootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", normalizedPath);
        var appDataPath = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", normalizedPath);
        var fullPath = System.IO.File.Exists(wwwrootPath) ? wwwrootPath : appDataPath;

        if (!System.IO.File.Exists(fullPath))
            return NotFound(new { success = false, message = "File not found" });

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".pdf" => "application/pdf",
            ".webp" => "image/webp",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => "application/octet-stream"
        };

        var stream = System.IO.File.OpenRead(fullPath);
        return File(stream, contentType);
    }

    /// <summary>
    /// Generates a cryptographically secure random password
    /// SECURITY: Uses RandomNumberGenerator for secure password generation
    /// </summary>
    private static string GenerateSecurePassword(int length)
    {
        const string uppercase = "ABCDEFGHJKLMNPQRSTUVWXYZ"; // Exclude I and O for clarity
        const string lowercase = "abcdefghijkmnopqrstuvwxyz"; // Exclude l for clarity
        const string digits = "23456789"; // Exclude 0 and 1 for clarity
        const string special = "!@#$%^&*";
        
        var allChars = uppercase + lowercase + digits + special;
        var password = new char[length];
        
        using (var rng = RandomNumberGenerator.Create())
        {
            // Ensure at least one character from each required set
            var bytes = new byte[4];
            rng.GetBytes(bytes);
            password[0] = uppercase[(int)(BitConverter.ToUInt32(bytes, 0) % (uint)uppercase.Length)];
            
            rng.GetBytes(bytes);
            password[1] = lowercase[(int)(BitConverter.ToUInt32(bytes, 0) % (uint)lowercase.Length)];
            
            rng.GetBytes(bytes);
            password[2] = digits[(int)(BitConverter.ToUInt32(bytes, 0) % (uint)digits.Length)];
            
            rng.GetBytes(bytes);
            password[3] = special[(int)(BitConverter.ToUInt32(bytes, 0) % (uint)special.Length)];
            
            // Fill the rest with random characters
            for (int i = 4; i < length; i++)
            {
                rng.GetBytes(bytes);
                password[i] = allChars[(int)(BitConverter.ToUInt32(bytes, 0) % (uint)allChars.Length)];
            }
        }
        
        // Shuffle the password to randomize position of required characters
        using (var rng = RandomNumberGenerator.Create())
        {
            for (int i = password.Length - 1; i > 0; i--)
            {
                var bytes = new byte[4];
                rng.GetBytes(bytes);
                int j = (int)(BitConverter.ToUInt32(bytes, 0) % (uint)(i + 1));
                (password[i], password[j]) = (password[j], password[i]);
            }
        }
        
        return new string(password);
    }
}

