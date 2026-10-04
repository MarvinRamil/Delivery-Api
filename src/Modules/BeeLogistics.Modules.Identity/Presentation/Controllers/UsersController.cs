using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Identity.Infrastructure;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Application.Services;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Infrastructure;
using BeeLogistics.Shared.Presentation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MassTransit;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Infrastructure.Security;
using System.Security.Claims;

namespace BeeLogistics.Modules.Identity.Presentation.Controllers;

[Route("api/users")]
public class UsersController : BaseController
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IVirusScanner _virusScanner;
    private readonly IImageProcessor _imageProcessor;
    private readonly IFileStorageService _fileStorageService;
    private readonly IConfiguration _configuration;
    private readonly IEmailService _emailService;
    private readonly IEmailTemplateService _emailTemplateService;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly IdentityAppDbContext _identityDbContext;
    private readonly ILogger<UsersController>? _logger;
    private readonly BeeLogistics.Modules.Verification.Application.Interfaces.IShiftCheckGate? _shiftCheckGate;
    private readonly long _maxFileSizeBytes;

    public UsersController(
        UserManager<ApplicationUser> userManager,
        IVirusScanner virusScanner,
        IImageProcessor imageProcessor,
        IFileStorageService fileStorageService,
        IConfiguration configuration,
        IEmailService emailService,
        IEmailTemplateService emailTemplateService,
        IPublishEndpoint publishEndpoint,
        IdentityAppDbContext identityDbContext,
        ILogger<UsersController>? logger = null,
        BeeLogistics.Modules.Verification.Application.Interfaces.IShiftCheckGate? shiftCheckGate = null)
    {
        _userManager = userManager;
        _virusScanner = virusScanner;
        _imageProcessor = imageProcessor;
        _fileStorageService = fileStorageService;
        _configuration = configuration;
        _emailService = emailService;
        _emailTemplateService = emailTemplateService;
        _publishEndpoint = publishEndpoint;
        _identityDbContext = identityDbContext;
        _logger = logger;
        _shiftCheckGate = shiftCheckGate;
        _maxFileSizeBytes = _configuration.GetValue<long>("FileUpload:MaxFileSizeBytes", 10_485_760); // Default 10MB
    }

    [HttpGet("me/export")]
    [Authorize]
    public async Task<IActionResult> ExportMyData(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return NotFound();

        // Gather identity data
        var identityData = new
        {
            Profile = new
            {
                user.Id,
                user.Email,
                user.FullName,
                user.PhoneNumber,
                user.Role,
                user.CreatedAt,
                user.IsActive,
                user.ProfilePictureUrl
            },
            Vehicle = new
            {
                user.VehiclePlate,
                user.VehicleModel,
                user.VehicleColor,
                user.VehicleType
            },
            Security = new
            {
                HasSecurityQuestions = !string.IsNullOrEmpty(user.SecurityAnswerHash1),
                LivenessVerifiedAt = user.LivenessVerifiedAt
            }
        };

        var export = new
        {
            Timestamp = DateTime.UtcNow,
            Identity = identityData,
            Message = "This document contains personal data associated with your identity account as required by the PH Data Privacy Act of 2012. Other module data (Drivers, Payments) can be requested separately."
        };

        return Ok(new { success = true, data = export });
    }

    [HttpDelete("me")]
    [Authorize]
    public async Task<IActionResult> DeleteMe(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return NotFound();

        _logger?.LogInformation("User {UserId} requested account deletion (Erasure)", userId);

        try
        {
            // 1. Anonymize Identity PII
            var deletedSuffix = user.Id.Split('-')[0];
            user.FullName = "Deleted User " + deletedSuffix;
            user.Email = $"deleted_{user.Id}@bee-app.tech";
            user.UserName = user.Email;
            user.PhoneNumber = "00000000000";
            user.IsActive = false;
            user.ProfilePictureUrl = null;
            user.VehiclePlate = "DELETED";
            
            // 2. Clear security data
            user.SecurityAnswerHash1 = null;
            user.SecurityAnswerHash2 = null;
            user.SecurityAnswerHash3 = null;
            user.LivenessVerifiedAt = null;

            // 3. Update record
            await _userManager.UpdateAsync(user);

            // 4. Publish Integration Event for other modules to wipe their data
            await _publishEndpoint.Publish<IUserDeletedIntegrationEvent>(new UserDeletedIntegrationEvent
            {
                UserId = user.Id,
                FullName = user.FullName,
                DeletedAt = DateTime.UtcNow
            }, ct);

            _logger?.LogInformation("Account {UserId} anonymized and deactivated successfully", userId);

            return Ok(new { success = true, message = "Your identity data has been anonymized and deactivated, as per your Right to Erasure." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during account deletion for user {UserId}", userId);
            return StatusCode(500, new { success = false, message = "An error occurred during deletion. Please contact support." });
        }
    }

    /// <summary>
    /// Admin password reset endpoint - for testing purposes
    [HttpGet]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public async Task<IActionResult> GetAll()
    {
        var users = await _userManager.Users
            .OrderBy(u => u.FullName)
            .Select(u => new TeamMemberDto(
                u.Id,
                u.Email!,
                u.FullName,
                u.Role,
                u.IsActive,
                u.CreatedAt,
                u.Role == UserRoles.Driver ? u.IsOnline : null
            ))
            .ToListAsync();

        return Ok(new { success = true, data = users });
    }

    [HttpPost]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public async Task<IActionResult> Create([FromBody] CreateTeamMemberRequest request)
    {
        var existingUser = await _userManager.FindByEmailAsync(request.Email);
        if (existingUser != null)
        {
            return BadRequest(new { success = false, message = "Email already registered" });
        }

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            FullName = request.FullName,
            Role = request.Role,
            IsActive = true,
            IsOnboarded = request.Role != UserRoles.Owner
        };

        var result = await _userManager.CreateAsync(user, request.Password);

        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            return BadRequest(new { success = false, message = errors });
        }

        await _userManager.AddToRoleAsync(user, request.Role);

        return Ok(new
        {
            success = true,
            data = new TeamMemberDto(
                user.Id,
                user.Email!,
                user.FullName,
                user.Role,
                user.IsActive,
                user.CreatedAt,
                user.Role == UserRoles.Driver ? user.IsOnline : null
            )
        });
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateTeamMemberRequest request)
    {
        var user = await _userManager.FindByIdAsync(id);

        if (user == null)
        {
            return NotFound(new { success = false, message = "User not found" });
        }

        user.FullName = request.FullName;

        if (!string.IsNullOrEmpty(request.Role) && request.Role != user.Role)
        {
            await _userManager.RemoveFromRoleAsync(user, user.Role);
            await _userManager.AddToRoleAsync(user, request.Role);
            user.Role = request.Role;
        }

        await _userManager.UpdateAsync(user);

        return Ok(new { success = true, message = "User updated" });
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public async Task<IActionResult> Delete(string id)
    {
        var user = await _userManager.FindByIdAsync(id);

        if (user == null)
        {
            return NotFound(new { success = false, message = "User not found" });
        }

        await _userManager.DeleteAsync(user);

        return Ok(new { success = true, message = "User deleted" });
    }


    [HttpPost("complete-onboarding")]
    [Authorize]
    public async Task<IActionResult> CompleteOnboarding([FromBody] CompleteOnboardingRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return NotFound(new { success = false, message = "User not found" });
        }

        // Drivers are onboarded only through driver-application approval, never self-service
        if (user.Role == UserRoles.Driver)
        {
            return BadRequest(new { success = false, message = "Drivers must submit a driver application and be approved before onboarding" });
        }

        // BusinessType ("Fleet"/"Individual") was the fleet-vs-solo split; #43 removed it.
        // The field is still accepted and ignored so older clients keep working.
        user.IsOnboarded = true;

        await _userManager.UpdateAsync(user);

        return Ok(new { success = true, message = "Onboarding completed" });
    }

    [HttpPatch("driver/status")]
    [Authorize(Roles = "Driver,Owner")]
    public async Task<IActionResult> UpdateDriverOnlineStatus([FromBody] UpdateDriverOnlineStatusRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return NotFound(new { success = false, message = "User not found" });
        }

        // Only drivers can update their online status
        if (user.Role != UserRoles.Driver)
        {
            return BadRequest(new { success = false, message = "Only drivers can update their online status" });
        }

        // Per-shift face check: block going online until the driver re-verifies their face
        // against the KYC reference selfie (app handles this code by opening /shift-check).
        if (request.IsOnline && _shiftCheckGate != null &&
            await _shiftCheckGate.RequiresCheckAsync(user.Id, user.LastFaceCheckAt))
        {
            return Conflict(new
            {
                success = false,
                code = "FACE_CHECK_REQUIRED",
                message = "Please complete a quick face check before going online."
            });
        }

        user.IsOnline = request.IsOnline;
        await _userManager.UpdateAsync(user);

        return Ok(new { success = true, message = "Online status updated", data = new { isOnline = user.IsOnline } });
    }

    /// <summary>
    /// Upload profile picture
    /// POST /api/users/{id}/profile-picture
    /// </summary>
    [HttpPost("{id}/profile-picture")]
    [Authorize]
    [RequestSizeLimit(5_500_000)] // 5.5MB max (5MB file + 0.5MB form overhead) - profile picture only
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadProfilePicture(string id, IFormFile image, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized("User ID not found in token");

        // SECURITY: Users can only upload their own profile picture
        if (userId != id)
        {
            return Forbid("You can only upload your own profile picture");
        }

        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound("User not found");

        if (image == null || image.Length == 0)
        {
            return BadRequest(new { success = false, message = "No image file provided" });
        }

        // SECURITY: File size validation
        if (image.Length > _maxFileSizeBytes)
        {
            var maxSizeMB = _maxFileSizeBytes / (1024.0 * 1024.0);
            return BadRequest(new { 
                success = false, 
                message = $"File size exceeds maximum allowed size of {maxSizeMB:F1} MB." 
            });
        }

        // SECURITY: File validation (extension, content type, magic bytes)
        if (!await FileUploadValidator.ValidateAsync(image, FileUploadValidator.ImageOnlyOptions(_maxFileSizeBytes), ct))
        {
            return BadRequest(new { 
                success = false, 
                message = "Only JPG and PNG image files are allowed. File content must match the file type." 
            });
        }

        // SECURITY: Virus scanning with ClamAV
        try
        {
            await using var scanStream = image.OpenReadStream();
            var scanResult = await _virusScanner.ScanFileAsync(scanStream, image.FileName, ct);
            
            if (scanResult.IsInfected)
            {
                var virusInfo = !string.IsNullOrEmpty(scanResult.VirusName) 
                    ? $" Detected threat: {scanResult.VirusName}" 
                    : "";
                return BadRequest(new { 
                    success = false, 
                    message = $"File upload rejected due to security threat.{virusInfo}" 
                });
            }

            if (scanResult.HasError)
            {
                // Fail-secure: Reject file if scanner is unavailable or errors occur
                var failSecure = _configuration.GetValue<bool>("FileUpload:FailSecureOnScanError", true);
                if (failSecure)
                {
                    return BadRequest(new { 
                        success = false, 
                        message = $"Security scan failed: {scanResult.ErrorMessage}. Please try again or contact support." 
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing profile picture upload");
            // Fail-secure: reject if scanning fails (configurable)
            var failSecure = _configuration.GetValue<bool>("FileUpload:FailSecureOnScanError", true);
            if (failSecure)
            {
                return BadRequest(new { 
                    success = false, 
                    message = "Security scan failed. Please try again or contact support." 
                });
            }
        }

        try
        {
            // Process image: convert to WebP, resize, and optimize
            await using var inputStream = image.OpenReadStream();
            await using var processedStream = new MemoryStream();
            
            var processingResult = await _imageProcessor.ProcessImageAsync(inputStream, processedStream, ct);
            
            if (!processingResult.Success)
            {
                return BadRequest(new { 
                    success = false, 
                    message = $"Image processing failed: {processingResult.ErrorMessage}" 
                });
            }

            // Generate secure filename: {userId}/{guid}.webp
            var fileGuid = Guid.NewGuid().ToString("N");
            var objectName = $"{user.Id}/{fileGuid}.webp";

            // Upload to S3
            processedStream.Position = 0;
            var imageUrl = await _fileStorageService.UploadFileAsync(
                processedStream,
                "profiles", // bucket name
                objectName, // object name: {userId}/{guid}.webp
                "image/webp",
                ct);

            // Delete old profile picture if exists
            if (!string.IsNullOrEmpty(user.ProfilePictureUrl))
            {
                try
                {
                    // Extract object name from old URL
                    var oldUrlParts = user.ProfilePictureUrl.Split('/');
                    if (oldUrlParts.Length >= 2)
                    {
                        var oldObjectName = string.Join("/", oldUrlParts.Skip(oldUrlParts.Length - 2));
                        await _fileStorageService.DeleteFileAsync("profiles", oldObjectName, ct);
                    }
                }
                catch
                {
                    // Continue even if deletion fails
                }
            }

            // Update user profile picture URL (store the raw S3/R2 URL in DB)
            user.ProfilePictureUrl = imageUrl;
            var updateResult = await _userManager.UpdateAsync(user);
            
            if (!updateResult.Succeeded)
            {
                return BadRequest(new { 
                    success = false, 
                    message = string.Join(", ", updateResult.Errors.Select(e => e.Description)) 
                });
            }

            // Return a presigned URL so the client can display the image immediately
            // R2 buckets are private, so the raw URL won't work for mobile clients
            string accessibleUrl;
            try
            {
                accessibleUrl = await _fileStorageService.GetPresignedUrlAsync("profiles", objectName, 7 * 24 * 3600, ct);
            }
            catch
            {
                accessibleUrl = imageUrl;
            }

            return Ok(new { 
                success = true, 
                message = "Profile picture uploaded successfully",
                data = new { imageUrl = accessibleUrl }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error uploading profile picture");
            return StatusCode(500, new { 
                success = false, 
                message = "An error occurred while uploading the profile picture. Please try again." 
            });
        }
    }

    /// <summary>
    /// List all drivers with vehicle info, online status, and profile pictures.
    /// GET /api/users/drivers?search=&amp;isOnline=true&amp;page=1&amp;pageSize=50&amp;masked=true
    /// SECURITY: Emails are masked by default (u***@domain.com). Pass masked=false for full emails.
    /// </summary>
    [HttpGet("drivers")]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public async Task<IActionResult> GetDrivers(
        [FromQuery] string? search = null,
        [FromQuery] bool? isOnline = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] bool masked = true,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 10;
        if (pageSize > 200) pageSize = 200;

        // Base query: all Driver-role users
        var query = _userManager.Users
            .AsNoTracking()
            .Where(u => u.Role == UserRoles.Driver);

        // Filter by online status
        if (isOnline.HasValue)
            query = query.Where(u => u.IsOnline == isOnline.Value);

        // Search by name or email
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(u =>
                u.FullName.ToLower().Contains(term) ||
                u.Email!.ToLower().Contains(term));
        }

        var totalCount = await query.CountAsync(ct);

        // Left-join primary vehicle from DriverVehicleAssignments
        var drivers = await query
            .OrderBy(u => u.FullName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .GroupJoin(
                _identityDbContext.DriverVehicleAssignments
                    .AsNoTracking()
                    .Include(a => a.Vehicle)
                    .Where(a => a.IsPrimary),
                u => u.Id,
                a => a.DriverId,
                (u, assignments) => new { User = u, Assignments = assignments })
            .SelectMany(
                x => x.Assignments.DefaultIfEmpty(),
                (x, assignment) => new DriverListDto(
                    x.User.Id,
                    x.User.FullName,
                    x.User.Email!,
                    x.User.IsActive,
                    x.User.IsOnline,
                    x.User.IsOnboarded,
                    x.User.ProfilePictureUrl,
                    assignment != null && assignment.Vehicle != null ? assignment.Vehicle.Type : x.User.VehicleType,
                    assignment != null && assignment.Vehicle != null ? assignment.Vehicle.Model : x.User.VehicleModel,
                    assignment != null && assignment.Vehicle != null ? assignment.Vehicle.Color : x.User.VehicleColor,
                    assignment != null && assignment.Vehicle != null ? assignment.Vehicle.PlateNumber : x.User.VehiclePlate,
                    x.User.CreatedAt,
                    x.User.LocationUpdatedAt))
            .ToListAsync(ct);

        // Post-processing: resolve presigned URLs and mask emails
        var resolved = new List<DriverListDto>(drivers.Count);
        foreach (var d in drivers)
        {
            var url = await ResolvePresignedUrlAsync(d.ProfilePictureUrl, ct);
            var email = masked ? MaskEmail(d.Email) : d.Email;
            resolved.Add(d with { ProfilePictureUrl = url, Email = email });
        }

        return Ok(new
        {
            success = true,
            data = resolved,
            totalCount,
            page,
            pageSize
        });
    }

    private async Task<string?> ResolvePresignedUrlAsync(string? storedUrl, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(storedUrl) || _fileStorageService == null)
            return storedUrl;
        try
        {
            // Stored format can be: (1) s3:profiles:objectName, or (2) full R2 URL
            if (storedUrl.StartsWith("s3:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = storedUrl.Split(':', 3, StringSplitOptions.None);
                if (parts.Length >= 3)
                    return await _fileStorageService.GetPresignedUrlAsync(parts[1], parts[2], 7 * 24 * 3600, ct);
                return storedUrl;
            }

            if (storedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || storedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(storedUrl);
                var segments = uri.AbsolutePath.TrimStart('/').Split('/', 2);
                if (segments.Length >= 2)
                    return await _fileStorageService.GetPresignedUrlWithFullKeyAsync(segments[0], segments[1], 7 * 24 * 3600, ct);
            }

            return storedUrl;
        }
        catch
        {
            return storedUrl;
        }
    }

    [HttpGet("drivers/online")]
    [Authorize(Roles = "SuperAdmin,Admin,Owner,Dispatcher")]
    public async Task<IActionResult> GetOnlineDrivers()
    {
        var query = _userManager.Users
            .Where(u => u.IsActive &&
                   u.IsOnline &&
                   u.Role == UserRoles.Driver);

        var onlineDrivers = await query
            .OrderBy(u => u.FullName)
            .Select(u => new OnlineDriverDto(
                Guid.Parse(u.Id),
                u.FullName,
                u.Role,
                u.CurrentLatitude,
                u.CurrentLongitude,
                u.LocationUpdatedAt,
                u.IsOnline
            ))
            .ToListAsync();

        return Ok(new { success = true, data = onlineDrivers });
    }

    [HttpGet("clients")]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public async Task<IActionResult> GetRegisteredClients([FromQuery] bool masked = true)
    {
        var clients = await _userManager.Users
            .Where(u => u.Role == UserRoles.Client || u.Role == UserRoles.BusinessClient)
            .OrderBy(u => u.FullName)
            .Select(u => new RegisteredClientDto(
                u.Id,
                masked ? MaskEmail(u.Email!) : u.Email!,
                u.FullName,
                u.Role,
                u.EmailConfirmed,
                u.IsActive,
                u.CreatedAt
            ))
            .ToListAsync();

        return Ok(new { success = true, data = clients });
    }

    private static string MaskEmail(string email)
    {
        if (string.IsNullOrEmpty(email)) return email;
        
        var parts = email.Split('@');
        if (parts.Length != 2) return email;
        
        var username = parts[0];
        var domain = parts[1];
        
        if (username.Length <= 1)
            return $"{username[0]}***@{domain}";
        
        return $"{username[0]}***@{domain}";
    }

    [HttpGet("driver/my-status")]
    [Authorize(Roles = "Driver,Owner")]
    public async Task<IActionResult> GetMyDriverStatus()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return NotFound(new { success = false, message = "User not found" });
        }

        // Only drivers and solo drivers can check their status
        if (user.Role != UserRoles.Driver)
        {
            return BadRequest(new { success = false, message = "Only drivers can check their status" });
        }

        return Ok(new { 
            success = true, 
            data = new { 
                isOnline = user.IsOnline,
                currentLatitude = user.CurrentLatitude,
                currentLongitude = user.CurrentLongitude,
                locationUpdatedAt = user.LocationUpdatedAt
            } 
        });
    }

    /// <summary>
    /// Admin password reset endpoint - for testing purposes
    /// Allows SuperAdmin/Admin to reset any user's password
    /// </summary>
    [HttpPost("{id}/reset-password")]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public async Task<IActionResult> ResetUserPassword(string id, [FromBody] AdminResetPasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
        {
            return BadRequest(new { success = false, message = "New password must be at least 8 characters" });
        }

        // Try to find user by ID first, then by email from request body (for CustomerDto which uses Customer entity ID)
        var user = await _userManager.FindByIdAsync(id);
        if (user == null && !string.IsNullOrWhiteSpace(request.Email))
        {
            // If ID lookup fails and email is provided, try finding by email
            user = await _userManager.FindByEmailAsync(request.Email);
        }
        
        if (user == null)
        {
            return NotFound(new { success = false, message = "User not found" });
        }

        try
        {
            // Generate reset token and reset password directly
            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, resetToken, request.NewPassword);

            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                _logger?.LogWarning("Admin password reset failed for user {UserId}: {Errors}", id, errors);
                return BadRequest(new { success = false, message = $"Password reset failed: {errors}" });
            }

            // Note: Token invalidation would require TokenBlacklistService and RefreshTokenService
            // For testing purposes, users will need to login again with the new password
            _logger?.LogInformation("Admin reset password for user {UserId} ({Email})", id, user.Email);

            return Ok(new { success = true, message = "Password reset successfully" });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error resetting password for user {UserId}", id);
            return BadRequest(new { success = false, message = "Failed to reset password. Please try again." });
        }
    }
}

public record TeamMemberDto(string Id, string Email, string FullName, string Role, bool IsActive, DateTime CreatedAt, bool? IsOnline = null);
public record RegisteredClientDto(string Id, string Email, string FullName, string Role, bool IsEmailVerified, bool IsActive, DateTime CreatedAt);
public record CreateTeamMemberRequest(string Email, string Password, string FullName, string Role);
public record UpdateTeamMemberRequest(string FullName, string? Role);
public record UpdateStatusRequest(bool IsActive);
// BusinessType is accepted and ignored since #43; kept so older clients still bind.
public record CompleteOnboardingRequest(string BusinessType);
public record UpdateDriverOnlineStatusRequest(bool IsOnline);
public record OnlineDriverDto(
    Guid Id,
    string FullName,
    string Role,
    decimal? CurrentLatitude,
    decimal? CurrentLongitude,
    DateTime? LocationUpdatedAt,
    bool IsOnline
);
public record DriverListDto(
    string Id,
    string FullName,
    string Email,
    bool IsActive,
    bool IsOnline,
    bool IsOnboarded,
    string? ProfilePictureUrl,
    string? VehicleType,
    string? VehicleModel,
    string? VehicleColor,
    string? VehiclePlate,
    DateTime CreatedAt,
    DateTime? LocationUpdatedAt
);
public record AdminResetPasswordRequest(string NewPassword, string? Email = null);