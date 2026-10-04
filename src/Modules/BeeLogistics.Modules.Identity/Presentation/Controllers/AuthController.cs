using BeeLogistics.Modules.Identity.Application;
using BeeLogistics.Modules.Identity.Application.Auth;
using BeeLogistics.Modules.Identity.Application.Auth.EmailVerification;
using BeeLogistics.Modules.Identity.Application.Auth.Otp;
using BeeLogistics.Modules.Identity.Application.Auth.Password;
using BeeLogistics.Modules.Identity.Application.Services;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Identity.Infrastructure;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Application.Services;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Presentation;
using MassTransit;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace BeeLogistics.Modules.Identity.Presentation.Controllers;

[Route("api/auth")]
public class AuthController : BaseController
{
    // SECURITY: Input validation constants (OWASP Top 10 - A03:2021 Injection)
    
    // SECURITY: Request size limits (OWASP Top 10 - A05:2021 Security Misconfiguration)
    private const int MAX_AUTH_REQUEST_SIZE_BYTES = 10240; // 10KB for auth requests
    
    // SECURITY: Standardized error messages (OWASP Top 10 - A09:2021 Security Logging)
    
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly IAuditService _auditService;
    private readonly ITokenBlacklistService _tokenBlacklistService;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IAuthTokenIssuer _tokenIssuer;
    private readonly IDriverVehicleService _driverVehicles;
    private readonly IRegistrationVerificationTokenService _registrationTokens;
    private readonly IProfilePictureUrlResolver _profilePictures;
    private readonly IMediator _mediator;
    private readonly IEmailService _emailService;
    private readonly IEmailTemplateService _emailTemplateService;
    private readonly ISmsNotificationService _smsNotificationService;
    private readonly OtpService _otpService;
    private readonly IVirusScanner? _virusScanner;
    private readonly IImageProcessor? _imageProcessor;
    private readonly IFileStorageService? _fileStorageService;
    private readonly IdentityAppDbContext _identityDbContext;
    private readonly ILogger<AuthController>? _logger;
    private readonly long _maxFileSizeBytes;
    private readonly IPublishEndpoint? _publishEndpoint;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IDataProtectorService? _dataProtector;
    private readonly ClerkUserProvisioningService? _clerkProvisioning;

    public AuthController(
        UserManager<ApplicationUser> userManager, 
        IConfiguration configuration,
        IAuditService auditService,
        ITokenBlacklistService tokenBlacklistService,
        IRefreshTokenService refreshTokenService,
        IAuthTokenIssuer tokenIssuer,
        IDriverVehicleService driverVehicles,
        IRegistrationVerificationTokenService registrationTokens,
        IProfilePictureUrlResolver profilePictures,
        IMediator mediator,
        IEmailService emailService,
        IEmailTemplateService emailTemplateService,
        ISmsNotificationService smsNotificationService,
        OtpService otpService,
        IdentityAppDbContext identityDbContext,
        IHttpContextAccessor httpContextAccessor,
        IDataProtectorService? dataProtector = null,
        ClerkUserProvisioningService? clerkProvisioning = null,
        IVirusScanner? virusScanner = null,
        IImageProcessor? imageProcessor = null,
        IFileStorageService? fileStorageService = null,
        ILogger<AuthController>? logger = null,
        IPublishEndpoint? publishEndpoint = null)
    {
        _userManager = userManager;
        _configuration = configuration;
        _auditService = auditService;
        _tokenBlacklistService = tokenBlacklistService;
        _refreshTokenService = refreshTokenService;
        _tokenIssuer = tokenIssuer;
        _driverVehicles = driverVehicles;
        _registrationTokens = registrationTokens;
        _profilePictures = profilePictures;
        _mediator = mediator;
        _emailService = emailService;
        _emailTemplateService = emailTemplateService;
        _smsNotificationService = smsNotificationService;
        _otpService = otpService;
        _identityDbContext = identityDbContext;
        _httpContextAccessor = httpContextAccessor;
        _dataProtector = dataProtector;
        _clerkProvisioning = clerkProvisioning;
        _virusScanner = virusScanner;
        _imageProcessor = imageProcessor;
        _fileStorageService = fileStorageService;
        _logger = logger;
        _publishEndpoint = publishEndpoint;
        _maxFileSizeBytes = _configuration.GetValue<long>("FileUpload:MaxFileSizeBytes", 10_485_760); // Default 10MB
    }
    
    /// <summary>
    /// SECURITY: Get request ID for correlation (OWASP Top 10 - A09:2021 Security Logging)
    /// </summary>
    private string GetRequestId() => AuthHelpers.GetRequestId(_httpContextAccessor.HttpContext);

    /// <summary>Maps the wire-level security answers onto the Application layer's input type.</summary>
    private static IReadOnlyList<SecurityAnswerInput>? ToAnswerInputs(List<SecurityAnswerRequest>? answers)
        => answers?.Select(a => new SecurityAnswerInput(a.QuestionNumber, a.Answer)).ToList();
    
    /// <summary>
    /// SECURITY: Get user agent from request (OWASP Top 10 - A09:2021 Security Logging)
    /// </summary>
    private string? GetUserAgent() => AuthHelpers.GetUserAgent(_httpContextAccessor.HttpContext);

    private async Task<UserInfo> BuildUserInfoAsync(ApplicationUser user, CancellationToken ct = default)
    {
        var primaryVehicle = await _driverVehicles.GetPrimaryVehicleInfoAsync(user.Id, ct);
        var profilePictureUrl = await _profilePictures.ResolveAsync(user.ProfilePictureUrl, ct);
        return new UserInfo(
            Id: user.Id,
            Email: user.Email!,
            FullName: user.FullName,
            Role: user.Role,
            TenantId: null,
            IsOnboarded: user.IsOnboarded,
            BusinessType: null,
            IsSoloDriver: false,
            ProfilePictureUrl: profilePictureUrl,
            LivenessVerifiedAt: user.LivenessVerifiedAt,
            VehiclePlate: primaryVehicle?.PlateNumber ?? user.VehiclePlate,
            VehicleModel: primaryVehicle?.Model ?? user.VehicleModel,
            VehicleColor: primaryVehicle?.Color ?? user.VehicleColor,
            VehicleType: primaryVehicle?.Type ?? user.VehicleType
        );
    }

    /// <summary>
    /// Login endpoint for backoffice (SuperAdmin/Admin only)
    /// </summary>
    [HttpPost("backoffice-login")]
    [AllowAnonymous]
    [RequestSizeLimit(MAX_AUTH_REQUEST_SIZE_BYTES)]
    public async Task<IActionResult> BackofficeLogin([FromBody] LoginRequest request)
    {
        // Cutover complete: admin auth lives in the dedicated back-office backend.
        // Disabled by default; set Features:BackofficeLoginEnabled=true only to temporarily re-open.
        if (!_configuration.GetValue("Features:BackofficeLoginEnabled", false))
        {
            return NotFound(new { success = false, message = "Backoffice login has moved. Use the back-office portal." });
        }

        // SECURITY: Input validation (OWASP Top 10 - A03:2021 Injection)
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new { success = false, message = AuthMessages.EmailRequired });
        }
        if (request.Email.Length > AuthHelpers.MaxEmailLength)
        {
            return BadRequest(new { success = false, message = AuthMessages.EmailTooLong });
        }
        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { success = false, message = AuthMessages.PasswordRequired });
        }
        if (request.Password.Length > AuthHelpers.MaxPasswordLength)
        {
            return BadRequest(new { success = false, message = AuthMessages.PasswordTooLong });
        }
        if (!AuthHelpers.IsValidEmailFormat(request.Email))
        {
            return BadRequest(new { success = false, message = AuthMessages.InvalidEmailFormat });
        }
        
        var requestId = GetRequestId();
        var userAgent = GetUserAgent();
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        
        var user = await _userManager.FindByEmailAsync(normalizedEmail);
        if (user == null)
        {
            await _auditService.LogAsync(AuditActions.LoginFailed, AuditCategories.Auth, 
                userEmail: normalizedEmail, isSuccess: false, errorMessage: "User not found",
                userAgent: userAgent, requestId: requestId);
            return Unauthorized(AuthMessages.InvalidCredentials);
        }
        
        // OWASP: Check account lockout (brute force protection)
        if (await _userManager.IsLockedOutAsync(user))
        {
            await _auditService.LogAsync(AuditActions.LoginFailed, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                isSuccess: false, errorMessage: "Account locked out",
                userAgent: userAgent, requestId: requestId);
            return Unauthorized(AuthMessages.AccountLocked);
        }

        if (!user.IsActive)
        {
            await _auditService.LogAsync(AuditActions.LoginFailed, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                isSuccess: false, errorMessage: "Account deactivated",
                userAgent: userAgent, requestId: requestId);
            return Unauthorized(AuthMessages.AccountDeactivated);
        }

        // Check if email is verified
        if (!user.EmailConfirmed)
        {
            await _auditService.LogAsync(AuditActions.LoginFailed, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                isSuccess: false, errorMessage: "Email not verified",
                userAgent: userAgent, requestId: requestId);
            return Unauthorized(AuthMessages.EmailNotVerified);
        }

        // CRITICAL: Only SuperAdmin and Admin can access backoffice
        if (user.Role != "SuperAdmin" && user.Role != "Admin")
        {
            await _auditService.LogAsync(AuditActions.LoginFailed, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                isSuccess: false, errorMessage: "Unauthorized role for backoffice access",
                userAgent: userAgent, requestId: requestId);
            return Unauthorized("Access denied. Backoffice is restricted to administrators only.");
        }

        var valid = await _userManager.CheckPasswordAsync(user, request.Password);
        if (!valid)
        {
            // OWASP: Increment failed access attempts
            await _userManager.AccessFailedAsync(user);
            
            await _auditService.LogAsync(AuditActions.LoginFailed, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                isSuccess: false, errorMessage: "Invalid password",
                userAgent: userAgent, requestId: requestId);
            return Unauthorized(AuthMessages.InvalidCredentials);
        }

        // Reset lockout count on successful login
        await _userManager.ResetAccessFailedCountAsync(user);

        var accessToken = await _tokenIssuer.IssueAsync(user, AuthTokenProfile.BackofficeLogin, HttpContext.RequestAborted);

        // Generate refresh token for backoffice users
        var refreshToken = await _refreshTokenService.GenerateRefreshTokenAsync(user.Id);

        // Log successful login
        await _auditService.LogAsync(AuditActions.Login, AuditCategories.Auth,
            userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
            details: "Backoffice login");

        // SECURITY: Return access token + refresh token for backoffice
        return Ok(new LoginResponse(
            Token: accessToken.Token,
            Expiration: accessToken.ExpiresAt,
            Role: user.Role,
            User: null, // Don't expose full user object in backoffice login response
            RefreshToken: refreshToken.Token,
            RefreshTokenExpiration: refreshToken.ExpiresAt
        ));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [RequestSizeLimit(MAX_AUTH_REQUEST_SIZE_BYTES)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        // SECURITY: Input validation (OWASP Top 10 - A03:2021 Injection)
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new { success = false, message = AuthMessages.EmailRequired });
        }
        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { success = false, message = AuthMessages.PasswordRequired });
        }
        if (request.Password.Length > AuthHelpers.MaxPasswordLength)
        {
            return BadRequest(new { success = false, message = AuthMessages.PasswordTooLong });
        }

        var requestId = GetRequestId();
        var userAgent = GetUserAgent();
        var identifier = request.Email.Trim();
        var looksLikeEmail = identifier.Contains('@');
        ApplicationUser? user;
        string? normalizedPhone = null;

        if (looksLikeEmail)
        {
            // Email login path (original behavior with email validation)
            if (identifier.Length > AuthHelpers.MaxEmailLength)
            {
                return BadRequest(new { success = false, message = AuthMessages.EmailTooLong });
            }
            if (!AuthHelpers.IsValidEmailFormat(identifier))
            {
                return BadRequest(new { success = false, message = AuthMessages.InvalidEmailFormat });
            }

            var normalizedEmail = identifier.ToLowerInvariant();
            user = await _userManager.FindByEmailAsync(normalizedEmail);
        }
        else
        {
            // Phone login path (SMS signup)
            static string Mask(string value)
            {
                if (string.IsNullOrEmpty(value)) return value;
                if (value.Length <= 4) return "****";
                var last4 = value[^4..];
                return $"****{last4}";
            }

            normalizedPhone = AuthHelpers.NormalizePhoneNumber(identifier);
            if (string.IsNullOrEmpty(normalizedPhone) || normalizedPhone.Length < 10 || normalizedPhone.Length > AuthHelpers.MaxPhoneLength)
            {
                return BadRequest(new { success = false, message = AuthMessages.InvalidPhoneFormat });
            }

            // UserName holds the normalized phone in plaintext for phone-based accounts, so the
            // login lookup matches on it directly (PhoneNumber itself is encrypted).
            user = await _identityDbContext.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.UserName == normalizedPhone);
        }

        if (user == null)
        {
            await _auditService.LogAsync(AuditActions.LoginFailed, AuditCategories.Auth, 
                userEmail: identifier.ToLowerInvariant(), isSuccess: false, errorMessage: "User not found",
                userAgent: userAgent, requestId: requestId);
            return Unauthorized(AuthMessages.InvalidCredentials);
        }

        // OWASP: Check account lockout (brute force protection)
        if (await _userManager.IsLockedOutAsync(user))
        {
            await _auditService.LogAsync(AuditActions.LoginFailed, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                isSuccess: false, errorMessage: "Account locked out",
                userAgent: userAgent, requestId: requestId);
            return Unauthorized(AuthMessages.AccountLocked);
        }

        if (!user.IsActive)
        {
            await _auditService.LogAsync(AuditActions.LoginFailed, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                isSuccess: false, errorMessage: "Account deactivated",
                userAgent: userAgent, requestId: requestId);
            return Unauthorized(AuthMessages.AccountDeactivated);
        }

        // Check if email is verified for email-based login.
        // For phone-based login, require only phone verification (EmailConfirmed may be false for phone-only signups).
        if (looksLikeEmail && !user.EmailConfirmed)
        {
            await _auditService.LogAsync(AuditActions.LoginFailed, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                isSuccess: false, errorMessage: "Email not verified",
                userAgent: userAgent, requestId: requestId);
            return Unauthorized(AuthMessages.EmailNotVerified);
        }

        var valid = await _userManager.CheckPasswordAsync(user, request.Password);
        if (!valid)
        {
            // OWASP: Increment failed access attempts
            await _userManager.AccessFailedAsync(user);
            
            await _auditService.LogAsync(AuditActions.LoginFailed, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                isSuccess: false, errorMessage: "Invalid password",
                userAgent: userAgent, requestId: requestId);
            return Unauthorized(AuthMessages.InvalidCredentials);
        }

        // Reset lockout count on successful login
        await _userManager.ResetAccessFailedCountAsync(user);

        var accessToken = await _tokenIssuer.IssueAsync(user, AuthTokenProfile.Frontend, HttpContext.RequestAborted);

        // Generate refresh token for frontend (e.g. driver app) so they can refresh on 401
        var refreshToken = await _refreshTokenService.GenerateRefreshTokenAsync(user.Id);

        // Log successful login
        await _auditService.LogAsync(AuditActions.Login, AuditCategories.Auth,
            userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role);

        // Frontend login - return minimal user info (only what's needed) and refresh token
        return Ok(new LoginResponse(
            Token: accessToken.Token,
            Expiration: accessToken.ExpiresAt,
            Role: user.Role,
            User: await BuildUserInfoAsync(user, HttpContext.RequestAborted),
            RefreshToken: refreshToken.Token,
            RefreshTokenExpiration: refreshToken.ExpiresAt
        ));
    }

    /// <summary>
    /// Send OTP for email verification (before account creation)
    /// SECURITY: Rate limited, generic response to prevent email enumeration
    /// </summary>
    [HttpPost("send-otp")]
    [AllowAnonymous]
    public async Task<IActionResult> SendOtp([FromBody] SendOtpRequest request)
    {
        var outcome = await _mediator.Send(new SendEmailOtpCommand(request.Email), HttpContext.RequestAborted);
        return outcome.Success
            ? Ok(new { success = true, message = outcome.Message })
            : BadRequest(new { success = false, message = outcome.Message });
    }

    /// <summary>
    /// Send OTP for phone verification (before account creation) via SMS.
    /// SECURITY: Rate limited (global middleware), generic responses to prevent phone enumeration.
    /// </summary>
    [HttpPost("send-sms-otp")]
    [AllowAnonymous]
    public async Task<IActionResult> SendSmsOtp([FromBody] SendSmsOtpRequest request)
    {
        var outcome = await _mediator.Send(new SendSmsOtpCommand(request.PhoneNumber), HttpContext.RequestAborted);
        return outcome.Success
            ? Ok(new { success = true, message = outcome.Message })
            : BadRequest(new { success = false, message = outcome.Message });
    }

    /// <summary>
    /// Verify OTP only (single responsibility). Does not create an account.
    /// On success, marks email as verified for registration; call Register next with same email.
    /// </summary>
    [HttpPost("verify-otp")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpRequest request)
    {
        var outcome = await _mediator.Send(new VerifyEmailOtpCommand(request.Email, request.Otp), HttpContext.RequestAborted);
        return outcome.Success
            ? Ok(new { success = true, message = outcome.Message, registrationToken = outcome.RegistrationToken })
            : BadRequest(new { success = false, message = outcome.Message });
    }

    /// <summary>
    /// Verify phone OTP only. Does not create an account.
    /// On success, marks phone as verified for registration; call register-by-phone next with same phone.
    /// </summary>
    [HttpPost("verify-sms-otp")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifySmsOtp([FromBody] VerifySmsOtpRequest request)
    {
        var outcome = await _mediator.Send(new VerifySmsOtpCommand(request.PhoneNumber, request.Otp), HttpContext.RequestAborted);
        return outcome.Success
            ? Ok(new { success = true, message = outcome.Message, registrationToken = outcome.RegistrationToken })
            : BadRequest(new { success = false, message = outcome.Message });
    }

    /// <summary>
    /// Resend phone OTP. Invalidates the previous OTP.
    /// </summary>
    [HttpPost("resend-sms-otp")]
    [AllowAnonymous]
    public async Task<IActionResult> ResendSmsOtp([FromBody] ResendSmsOtpRequest request)
    {
        var outcome = await _mediator.Send(new ResendSmsOtpCommand(request.PhoneNumber), HttpContext.RequestAborted);
        return outcome.Success
            ? Ok(new { success = true, message = outcome.Message })
            : BadRequest(new { success = false, message = outcome.Message });
    }

    /// <summary>
    /// [Obsolete] Use verify-otp then register then login instead. Kept for backward compatibility.
    /// Verify OTP and register account (OTP-first registration flow)
    /// SECURITY: Rate limited, validates OTP before creating account
    /// </summary>
    [Obsolete("Use POST /api/auth/verify-otp then POST /api/auth/register then POST /api/auth/login instead.")]
    [HttpPost("verify-otp-and-register")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyOtpAndRegister([FromBody] VerifyOtpAndRegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || 
            string.IsNullOrWhiteSpace(request.Otp) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            string.IsNullOrWhiteSpace(request.FullName))
        {
            return BadRequest(new { success = false, message = "Email, OTP, password, and full name are required" });
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        // SECURITY: Check if email is locked
        if (await _otpService.IsEmailLockedAsync(normalizedEmail))
        {
            return BadRequest(new { 
                success = false, 
                message = "Too many failed attempts. Please try again later." 
            });
        }

        // Validate OTP first (before creating account)
        var otpResult = await _otpService.ValidateEmailVerificationOtpAsync(normalizedEmail, request.Otp);
        if (!otpResult.IsValid)
        {
            return BadRequest(new { success = false, message = otpResult.Message });
        }

        // OTP is valid - proceed with registration
        try
        {
            // Check if user already exists (double-check)
            var existingUser = await _userManager.FindByEmailAsync(normalizedEmail);
            if (existingUser != null)
            {
                return BadRequest(new { 
                    success = false, 
                    message = "This email is already registered. Please sign in instead." 
                });
            }

            // SECURITY: never let an anonymous caller self-assign a privileged role.
            if (!AuthHelpers.IsSelfRegistrableRole(request.Role))
                return BadRequest(new { success = false, message = "Invalid role for registration." });

            // Create user account
            var user = new ApplicationUser
            {
                UserName = normalizedEmail,
                Email = normalizedEmail,
                FullName = request.FullName.Trim(),
                Role = request.Role ?? UserRoles.Driver, // Default to Driver for mobile app
                EmailConfirmed = true, // OTP verification counts as email confirmation
                IsOnboarded = false, // Will be set to true after driver application approval
                IsActive = true
            };

            var result = await _userManager.CreateAsync(user, request.Password);
            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                _logger?.LogWarning("User creation failed after OTP verification for {Email}: {Errors}", normalizedEmail, errors);
                return BadRequest(new { 
                    success = false, 
                    message = "Registration failed. Please check your input and try again." 
                });
            }

            // Add to role
            await _userManager.AddToRoleAsync(user, user.Role);

            if (_publishEndpoint != null)
            {
                try
                {
                    await _publishEndpoint.Publish(new UserRegisteredEvent
                    {
                        UserId = Guid.Parse(user.Id),
                        DeviceId = request.DeviceId,
                        DeviceFingerprint = request.DeviceFingerprint,
                        RegisteredAt = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to publish UserRegisteredEvent for {UserId}", user.Id);
                }
            }

            // Process referral code if provided
            if (!string.IsNullOrWhiteSpace(request.ReferralCode))
            {
                try
                {
                    var referralCode = request.ReferralCode.Trim().ToUpperInvariant();
                    var referredUserId = user.Id;
                    var referredUserType = user.Role == UserRoles.Driver ? "Driver" : "Customer";
                    
                    var processReferralCommand = new ProcessReferralOnRegistrationCommand(
                        referralCode,
                        referredUserId,
                        referredUserType
                    );
                    
                    var referralResult = await _mediator.Send(processReferralCommand);
                    if (!referralResult.IsSuccess)
                    {
                        _logger?.LogWarning("Referral processing failed for user {UserId}: {Error}", user.Id, referralResult.Error);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error processing referral code during OTP registration");
                }
            }

            // Auto-login: Generate JWT token
            var accessToken = await _tokenIssuer.IssueAsync(user, AuthTokenProfile.Frontend, HttpContext.RequestAborted);

            // Generate refresh token
            var refreshToken = await _refreshTokenService.GenerateRefreshTokenAsync(user.Id);

            // Log successful registration
            await _auditService.LogAsync(AuditActions.UserCreated, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                details: "User registered via OTP verification");

            return Ok(new
            {
                success = true,
                message = "Account created successfully",
                data = new
                {
                    token = accessToken.Token,
                    expiration = accessToken.ExpiresAt,
                    refreshToken = refreshToken.Token,
                    refreshTokenExpiration = refreshToken.ExpiresAt,
                    user = await BuildUserInfoAsync(user, HttpContext.RequestAborted)
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during OTP registration for {Email}", normalizedEmail);
            return StatusCode(500, new { 
                success = false, 
                message = "An error occurred during registration. Please try again." 
            });
        }
    }

    /// <summary>
    /// Resend OTP for email verification
    /// SECURITY: Rate limited, invalidates previous OTP
    /// </summary>
    [HttpPost("resend-otp")]
    [AllowAnonymous]
    public async Task<IActionResult> ResendOtp([FromBody] ResendOtpRequest request)
    {
        var outcome = await _mediator.Send(new ResendEmailOtpCommand(request.Email), HttpContext.RequestAborted);
        return outcome.Success
            ? Ok(new { success = true, message = outcome.Message })
            : BadRequest(new { success = false, message = outcome.Message });
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        // SECURITY: Generate unique error reference for tracking
        var errorReference = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        
        try
        {
            var existingUser = await _userManager.FindByEmailAsync(request.Email);
            if (existingUser != null)
            {
                _logger?.LogWarning("Registration attempt with existing email: {Email}. ErrorRef: {ErrorRef}", 
                    request.Email, errorReference);
                return BadRequest(new { 
                    success = false, 
                    message = "This email is already registered. Please use a different email or sign in.",
                    errorReference = errorReference
                });
            }

            // If email was verified via OTP: prefer stateless token (works across API instances), else consume cache marker
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            var emailVerifiedByOtp = _registrationTokens.Validate(request.RegistrationToken, normalizedEmail)
                || await _otpService.ConsumeEmailVerifiedForRegistrationAsync(normalizedEmail);
            _logger?.LogInformation("Register for {Email}: emailVerifiedByOtp={EmailVerifiedByOtp} (token or cache)", normalizedEmail, emailVerifiedByOtp);

        // Validate security questions if provided (user can set up to 3)
        var securityQuestions = new List<(int QuestionId, string Answer)>();
        
        if (request.SecurityQuestion1?.QuestionId.HasValue == true)
        {
            if (!SecurityQuestionsService.IsValidQuestionId(request.SecurityQuestion1.QuestionId.Value))
            {
                _logger?.LogWarning("Invalid security question 1 ID in registration. ErrorRef: {ErrorRef}", errorReference);
                return BadRequest(new { success = false, message = "Invalid security question selected.", errorReference = errorReference });
            }
            if (string.IsNullOrWhiteSpace(request.SecurityQuestion1.Answer))
            {
                _logger?.LogWarning("Missing security answer 1 in registration. ErrorRef: {ErrorRef}", errorReference);
                return BadRequest(new { success = false, message = "Security answer is required.", errorReference = errorReference });
            }
            securityQuestions.Add((request.SecurityQuestion1.QuestionId.Value, request.SecurityQuestion1.Answer));
        }
        
        if (request.SecurityQuestion2?.QuestionId.HasValue == true)
        {
            if (!SecurityQuestionsService.IsValidQuestionId(request.SecurityQuestion2.QuestionId.Value))
            {
                _logger?.LogWarning("Invalid security question 2 ID in registration. ErrorRef: {ErrorRef}", errorReference);
                return BadRequest(new { success = false, message = "Invalid security question selected.", errorReference = errorReference });
            }
            if (string.IsNullOrWhiteSpace(request.SecurityQuestion2.Answer))
            {
                _logger?.LogWarning("Missing security answer 2 in registration. ErrorRef: {ErrorRef}", errorReference);
                return BadRequest(new { success = false, message = "Security answer is required.", errorReference = errorReference });
            }
            securityQuestions.Add((request.SecurityQuestion2.QuestionId.Value, request.SecurityQuestion2.Answer));
        }
        
        if (request.SecurityQuestion3?.QuestionId.HasValue == true)
        {
            if (!SecurityQuestionsService.IsValidQuestionId(request.SecurityQuestion3.QuestionId.Value))
            {
                _logger?.LogWarning("Invalid security question 3 ID in registration. ErrorRef: {ErrorRef}", errorReference);
                return BadRequest(new { success = false, message = "Invalid security question selected.", errorReference = errorReference });
            }
            if (string.IsNullOrWhiteSpace(request.SecurityQuestion3.Answer))
            {
                _logger?.LogWarning("Missing security answer 3 in registration. ErrorRef: {ErrorRef}", errorReference);
                return BadRequest(new { success = false, message = "Security answer is required.", errorReference = errorReference });
            }
            securityQuestions.Add((request.SecurityQuestion3.QuestionId.Value, request.SecurityQuestion3.Answer));
        }

        // SECURITY: never let an anonymous caller self-assign a privileged role.
        if (!AuthHelpers.IsSelfRegistrableRole(request.Role))
            return BadRequest(new { success = false, message = "Invalid role for registration." });

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            FullName = request.FullName,
            PhoneNumber = request.PhoneNumber,
            PhoneNumberConfirmed = false,
            EmailConfirmed = emailVerifiedByOtp, // true when user called verify-otp first
            Role = request.Role ?? UserRoles.Client,
            IsOnboarded = request.Role != UserRoles.Owner && request.Role != UserRoles.Driver,
            SecurityQuestionId1 = securityQuestions.Count > 0 ? securityQuestions[0].QuestionId : null,
            SecurityAnswerHash1 = securityQuestions.Count > 0 ? AuthHelpers.HashSecurityAnswer(securityQuestions[0].Answer) : null,
            SecurityQuestionId2 = securityQuestions.Count > 1 ? securityQuestions[1].QuestionId : null,
            SecurityAnswerHash2 = securityQuestions.Count > 1 ? AuthHelpers.HashSecurityAnswer(securityQuestions[1].Answer) : null,
            SecurityQuestionId3 = securityQuestions.Count > 2 ? securityQuestions[2].QuestionId : null,
            SecurityAnswerHash3 = securityQuestions.Count > 2 ? AuthHelpers.HashSecurityAnswer(securityQuestions[2].Answer) : null
        };

        var result = await _userManager.CreateAsync(user, request.Password);

        if (!result.Succeeded)
        {
            _logger?.LogWarning("User creation failed for {Email}. Errors: {Errors}. ErrorRef: {ErrorRef}", 
                request.Email, string.Join(", ", result.Errors.Select(e => e.Description)), errorReference);
            
            // SECURITY: Don't expose detailed error messages, provide generic message with error reference
            var errorMessages = result.Errors.Select(e => e.Description).ToList();
            var genericMessage = errorMessages.Any(e => e.Contains("Password") || e.Contains("password"))
                ? "Password does not meet requirements."
                : errorMessages.Any(e => e.Contains("Email") || e.Contains("email"))
                ? "Invalid email format."
                : "Registration failed. Please check your input and try again.";
            
            return BadRequest(new { 
                success = false, 
                message = $"{genericMessage} (Ref: {errorReference})",
                errorReference = errorReference
            });
        }

        // Add to role
        await _userManager.AddToRoleAsync(user, user.Role);

        // Publish for fraud detection (same device / multi-account)
        if (_publishEndpoint != null)
        {
            try
            {
                await _publishEndpoint.Publish(new UserRegisteredEvent
                {
                    UserId = Guid.Parse(user.Id),
                    DeviceId = request.DeviceId,
                    DeviceFingerprint = request.DeviceFingerprint,
                    RegisteredAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to publish UserRegisteredEvent for {UserId}", user.Id);
            }
        }

        // Send email verification only when email was not already verified via OTP
        if (!emailVerifiedByOtp)
        {
            try
            {
                var emailToken = await _userManager.GenerateEmailConfirmationTokenAsync(user);
                var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(emailToken));
                var verificationEmail = _emailTemplateService.CreateVerificationEmail(
                    user.Email!,
                    user.FullName,
                    encodedToken
                );
                await _emailService.SendAsync(verificationEmail);
                _logger?.LogInformation("Verification email sent to {Email}", user.Email);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to send verification email to {Email}", user.Email);
            }
        }

        // Process referral code if provided (using MediatR for cross-module communication)
        if (!string.IsNullOrWhiteSpace(request.ReferralCode))
        {
            try
            {
                // Use shared contract to avoid circular dependency
                var referralCode = request.ReferralCode.Trim().ToUpperInvariant();
                var referredUserId = user.Id;
                
                // Determine user type
                var referredUserType = request.Role == UserRoles.Driver || request.Role == UserRoles.Owner 
                    ? "Driver" 
                    : "Customer";
                
                var processReferralCommand = new ProcessReferralOnRegistrationCommand(
                    referralCode,
                    referredUserId,
                    referredUserType
                );
                
                var referralResult = await _mediator.Send(processReferralCommand);
                // Don't fail registration if referral processing fails, just log it
                if (!referralResult.IsSuccess)
                {
                    _logger?.LogWarning("Referral processing failed for user {UserId}: {Error}", user.Id, referralResult.Error);
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail registration
                _logger?.LogError(ex, "Error processing referral code during registration");
            }
        }

        // Return response indicating email verification is required
        return Ok(new { 
            success = true,
            message = "User registered successfully. Please check your email to verify your account.",
            requiresEmailVerification = true,
            email = user.Email
        });
        }
        catch (Exception ex)
        {
            // SECURITY: Log full error details server-side, return generic message to client
            _logger?.LogError(ex, "Unexpected error during registration for {Email}. ErrorRef: {ErrorRef}", 
                request?.Email ?? "unknown", errorReference);
            
            return StatusCode(500, new { 
                success = false, 
                message = "An unexpected error occurred during registration. Please try again later.",
                errorReference = errorReference
            });
        }
    }

    /// <summary>
    /// Register a new account using phone number + SMS OTP verification.
    /// Email is not required; a placeholder email is generated to satisfy unique email constraints.
    /// </summary>
    [HttpPost("register-by-phone")]
    [AllowAnonymous]
    public async Task<IActionResult> RegisterByPhone([FromBody] RegisterByPhoneRequest request)
    {
        // SECURITY: Generate unique error reference for tracking
        var errorReference = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        try
        {
            if (string.IsNullOrWhiteSpace(request.PhoneNumber) ||
                string.IsNullOrWhiteSpace(request.Password) ||
                string.IsNullOrWhiteSpace(request.FullName))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Phone number, password, and full name are required.",
                    errorReference = errorReference
                });
            }

            var normalizedPhone = AuthHelpers.NormalizePhoneNumber(request.PhoneNumber);
            if (string.IsNullOrEmpty(normalizedPhone) || normalizedPhone.Length < 10 || normalizedPhone.Length > AuthHelpers.MaxPhoneLength)
            {
                return BadRequest(new { success = false, message = AuthMessages.InvalidPhoneFormat, errorReference });
            }

            // SECURITY: Check if phone already exists. PhoneNumber is encrypted, so match on its
            // deterministic blind index.
            var normalizedPhoneHash = _dataProtector?.ComputeBlindIndex(normalizedPhone, "phone");
            var existingUser = await _identityDbContext.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => EF.Property<string>(u, "PhoneNumberHash") == normalizedPhoneHash);
            if (existingUser != null)
            {
                _logger?.LogWarning("Registration attempt with existing phone: {Phone}. ErrorRef: {ErrorRef}",
                    normalizedPhone, errorReference);
                return BadRequest(new
                {
                    success = false,
                    message = "This phone number is already registered. Please use a different phone or sign in.",
                    errorReference = errorReference
                });
            }

            // Verify phone was confirmed via OTP: prefer stateless token, else consume cache marker
            var phoneVerifiedByOtp = !string.IsNullOrWhiteSpace(request.RegistrationToken) &&
                                     _registrationTokens.Validate(request.RegistrationToken, normalizedPhone)
                                     || await _otpService.ConsumePhoneVerifiedForRegistrationAsync(normalizedPhone);

            _logger?.LogInformation("Register-by-phone for {Phone}: phoneVerifiedByOtp={PhoneVerifiedByOtp} (token or cache)",
                normalizedPhone, phoneVerifiedByOtp);

            if (!phoneVerifiedByOtp)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Phone verification is required before registration.",
                    errorReference = errorReference
                });
            }

            // Validate security questions if provided (user can set up to 3)
            var securityQuestions = new List<(int QuestionId, string Answer)>();

            if (request.SecurityQuestion1?.QuestionId.HasValue == true)
            {
                if (!SecurityQuestionsService.IsValidQuestionId(request.SecurityQuestion1.QuestionId.Value))
                {
                    _logger?.LogWarning("Invalid security question 1 ID in phone registration. ErrorRef: {ErrorRef}", errorReference);
                    return BadRequest(new { success = false, message = "Invalid security question selected.", errorReference = errorReference });
                }
                if (string.IsNullOrWhiteSpace(request.SecurityQuestion1.Answer))
                {
                    _logger?.LogWarning("Missing security answer 1 in phone registration. ErrorRef: {ErrorRef}", errorReference);
                    return BadRequest(new { success = false, message = "Security answer is required.", errorReference = errorReference });
                }
                securityQuestions.Add((request.SecurityQuestion1.QuestionId.Value, request.SecurityQuestion1.Answer));
            }

            if (request.SecurityQuestion2?.QuestionId.HasValue == true)
            {
                if (!SecurityQuestionsService.IsValidQuestionId(request.SecurityQuestion2.QuestionId.Value))
                {
                    _logger?.LogWarning("Invalid security question 2 ID in phone registration. ErrorRef: {ErrorRef}", errorReference);
                    return BadRequest(new { success = false, message = "Invalid security question selected.", errorReference = errorReference });
                }
                if (string.IsNullOrWhiteSpace(request.SecurityQuestion2.Answer))
                {
                    _logger?.LogWarning("Missing security answer 2 in phone registration. ErrorRef: {ErrorRef}", errorReference);
                    return BadRequest(new { success = false, message = "Security answer is required.", errorReference = errorReference });
                }
                securityQuestions.Add((request.SecurityQuestion2.QuestionId.Value, request.SecurityQuestion2.Answer));
            }

            if (request.SecurityQuestion3?.QuestionId.HasValue == true)
            {
                if (!SecurityQuestionsService.IsValidQuestionId(request.SecurityQuestion3.QuestionId.Value))
                {
                    _logger?.LogWarning("Invalid security question 3 ID in phone registration. ErrorRef: {ErrorRef}", errorReference);
                    return BadRequest(new { success = false, message = "Invalid security question selected.", errorReference = errorReference });
                }
                if (string.IsNullOrWhiteSpace(request.SecurityQuestion3.Answer))
                {
                    _logger?.LogWarning("Missing security answer 3 in phone registration. ErrorRef: {ErrorRef}", errorReference);
                    return BadRequest(new { success = false, message = "Security answer is required.", errorReference = errorReference });
                }
                securityQuestions.Add((request.SecurityQuestion3.QuestionId.Value, request.SecurityQuestion3.Answer));
            }

            // SECURITY: never let an anonymous caller self-assign a privileged role.
            if (!AuthHelpers.IsSelfRegistrableRole(request.Role))
                return BadRequest(new { success = false, message = "Invalid role for registration." });

            // Generate placeholder email to satisfy Identity's unique email requirement
            var placeholderEmail = $"{normalizedPhone}@phone.mybeeapp.local";

            var user = new ApplicationUser
            {
                UserName = normalizedPhone,
                Email = placeholderEmail,
                FullName = request.FullName,
                PhoneNumber = normalizedPhone,
                PhoneNumberConfirmed = true,
                EmailConfirmed = false, // Email is placeholder; not verified
                Role = request.Role ?? UserRoles.Client,
                IsOnboarded = request.Role != UserRoles.Owner && request.Role != UserRoles.Driver,
                SecurityQuestionId1 = securityQuestions.Count > 0 ? securityQuestions[0].QuestionId : null,
                SecurityAnswerHash1 = securityQuestions.Count > 0 ? AuthHelpers.HashSecurityAnswer(securityQuestions[0].Answer) : null,
                SecurityQuestionId2 = securityQuestions.Count > 1 ? securityQuestions[1].QuestionId : null,
                SecurityAnswerHash2 = securityQuestions.Count > 1 ? AuthHelpers.HashSecurityAnswer(securityQuestions[1].Answer) : null,
                SecurityQuestionId3 = securityQuestions.Count > 2 ? securityQuestions[2].QuestionId : null,
                SecurityAnswerHash3 = securityQuestions.Count > 2 ? AuthHelpers.HashSecurityAnswer(securityQuestions[2].Answer) : null
            };

            var result = await _userManager.CreateAsync(user, request.Password);

            if (!result.Succeeded)
            {
                _logger?.LogWarning("User creation failed for phone {Phone}. Errors: {Errors}. ErrorRef: {ErrorRef}",
                    normalizedPhone, string.Join(", ", result.Errors.Select(e => e.Description)), errorReference);

                var errorMessages = result.Errors.Select(e => e.Description).ToList();
                var genericMessage = errorMessages.Any(e => e.Contains("Password", StringComparison.OrdinalIgnoreCase))
                    ? "Password does not meet requirements."
                    : "Registration failed. Please check your input and try again.";

                return BadRequest(new
                {
                    success = false,
                    message = $"{genericMessage} (Ref: {errorReference})",
                    errorReference = errorReference
                });
            }

            // Add to role
            await _userManager.AddToRoleAsync(user, user.Role);

            // Publish for fraud detection (same device / multi-account)
            if (_publishEndpoint != null)
            {
                try
                {
                    await _publishEndpoint.Publish(new UserRegisteredEvent
                    {
                        UserId = Guid.Parse(user.Id),
                        DeviceId = request.DeviceId,
                        DeviceFingerprint = request.DeviceFingerprint,
                        RegisteredAt = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to publish UserRegisteredEvent for {UserId}", user.Id);
                }
            }

            // Process referral code if provided
            if (!string.IsNullOrWhiteSpace(request.ReferralCode))
            {
                try
                {
                    var referralCode = request.ReferralCode.Trim().ToUpperInvariant();
                    var referredUserId = user.Id;

                    var referredUserType = request.Role == UserRoles.Driver || request.Role == UserRoles.Owner
                        ? "Driver"
                        : "Customer";

                    var processReferralCommand = new ProcessReferralOnRegistrationCommand(
                        referralCode,
                        referredUserId,
                        referredUserType
                    );

                    var referralResult = await _mediator.Send(processReferralCommand);
                    if (!referralResult.IsSuccess)
                    {
                        _logger?.LogWarning("Referral processing failed for user {UserId} (phone registration): {Error}", user.Id, referralResult.Error);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error processing referral code during phone registration");
                }
            }

            return Ok(new
            {
                success = true,
                message = "User registered successfully using phone number.",
                requiresEmailVerification = false,
                phoneNumber = user.PhoneNumber
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error during phone registration for {Phone}. ErrorRef: {ErrorRef}",
                request?.PhoneNumber ?? "unknown", errorReference);

            return StatusCode(500, new
            {
                success = false,
                message = "An unexpected error occurred during registration. Please try again later.",
                errorReference = errorReference
            });
        }
    }

    /// <summary>
    /// Get current user info. Returns minimal data based on user type.
    /// SECURITY: Only expose what the frontend absolutely needs for UI rendering.
    /// Authorization decisions are made server-side from JWT, not from this response.
    /// </summary>
    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentUser()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            // Authenticated via Clerk but the local profile doesn't exist yet
            // (the user.created webhook can lag behind sign-in, or be delayed/down).
            // When unlinked, NameIdentifier is still the Clerk id (the claims
            // transform only remaps it once a local user exists). Provision the
            // local user just-in-time from the Clerk Backend API so sign-in never
            // depends on webhook timing. EnsureLocalUserAsync is gated on
            // ClerkUserId, so if the webhook already created the user this is a
            // no-op lookup.
            if (_clerkProvisioning != null)
            {
                var clerkUserId = User.FindFirstValue("clerk_id") ?? userId;
                // The calling app supplies the role for a brand-new user via this
                // header (driver app sends "driver"); absent/anything else → Customer.
                var roleHint = Request.Headers["X-Bee-Role"].FirstOrDefault();
                user = await _clerkProvisioning.EnsureLocalUserAsync(clerkUserId, roleHint, HttpContext.RequestAborted);
            }
            if (user == null) return NotFound(AuthMessages.UserNotFound);
        }

        // SECURITY: Check if this is a backoffice user (SuperAdmin/Admin)
        // Backoffice users get minimal response - only what's needed for UI
        var isBackoffice = User.FindFirst("is_backoffice")?.Value == "true" ||
                          user.Role == "SuperAdmin" || user.Role == "Admin";

        if (isBackoffice)
        {
            // SECURITY: Minimal response for backoffice - no IDs, no tenant info
            // Only expose what's needed for UI display (name, email, role)
            return Ok(new BackofficeUserInfo(
                FullName: user.FullName,
                Email: user.Email!,
                Role: user.Role
            ));
        }

        // Frontend users (mobile apps) - return standard info
        return Ok(await BuildUserInfoAsync(user, HttpContext.RequestAborted));
    }

    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return NotFound(AuthMessages.UserNotFound);

        if (!string.IsNullOrWhiteSpace(request.FullName))
        {
            user.FullName = request.FullName.Trim();
        }

        // Driver vehicle fields (legacy fields kept for compatibility + synced to relation model).
        // VehicleType is deliberately NOT settable here: it is tied to the OR/CR, LTFRB PA and
        // insurance documents reviewed at application time, and it determines the driver's fare
        // class. It is set only on application approval, validated against the vehicle pricing
        // table. Older app versions still send the field; it is ignored rather than rejected so
        // an unrelated plate or colour correction from an un-updated binary still succeeds.
        if (request.VehiclePlate != null) user.VehiclePlate = string.IsNullOrWhiteSpace(request.VehiclePlate) ? null : request.VehiclePlate.Trim();
        if (request.VehicleModel != null) user.VehicleModel = string.IsNullOrWhiteSpace(request.VehicleModel) ? null : request.VehicleModel.Trim();
        if (request.VehicleColor != null) user.VehicleColor = string.IsNullOrWhiteSpace(request.VehicleColor) ? null : request.VehicleColor.Trim();

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return BadRequest(new { success = false, message = string.Join(", ", result.Errors.Select(e => e.Description)) });
        }

        if (request.VehiclePlate != null || request.VehicleModel != null || request.VehicleColor != null)
        {
            await _driverVehicles.UpsertPrimaryVehicleAssignmentAsync(user, HttpContext.RequestAborted);
        }

        return Ok(new { success = true, message = "Profile updated successfully" });
    }

    [HttpGet("me/vehicles")]
    public async Task<IActionResult> GetMyVehicles(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var vehicles = await _identityDbContext.DriverVehicleAssignments
            .AsNoTracking()
            .Where(x => x.DriverId == userId)
            .OrderByDescending(x => x.IsPrimary)
            .ThenByDescending(x => x.AssignedAt)
            .Select(x => new DriverVehicleDto(
                x.VehicleId,
                x.Vehicle.PlateNumber,
                x.Vehicle.Model,
                x.Vehicle.Color,
                x.Vehicle.Type,
                x.IsPrimary,
                x.AssignedAt
            ))
            .ToListAsync(ct);

        return Ok(vehicles);
    }

    [HttpPost("me/vehicles")]
    public async Task<IActionResult> AddVehicleToMe([FromBody] UpsertDriverVehicleRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.PlateNumber))
        {
            return BadRequest(new { success = false, message = "Plate number is required" });
        }

        var normalizedPlate = request.PlateNumber.Trim().ToUpperInvariant();
        var vehicle = await _identityDbContext.Vehicles.FirstOrDefaultAsync(v => v.PlateNumber == normalizedPlate, ct);
        if (vehicle == null)
        {
            vehicle = new Vehicle
            {
                PlateNumber = normalizedPlate,
                Model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim(),
                Color = string.IsNullOrWhiteSpace(request.Color) ? null : request.Color.Trim()
            };
            _identityDbContext.Vehicles.Add(vehicle);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(request.Model)) vehicle.Model = request.Model.Trim();
            if (!string.IsNullOrWhiteSpace(request.Color)) vehicle.Color = request.Color.Trim();
        }

        var existing = await _identityDbContext.DriverVehicleAssignments
            .FirstOrDefaultAsync(x => x.DriverId == userId && x.VehicleId == vehicle.Id, ct);
        if (existing == null)
        {
            _identityDbContext.DriverVehicleAssignments.Add(new DriverVehicleAssignment
            {
                DriverId = userId,
                Vehicle = vehicle,
                IsPrimary = request.IsPrimary ?? false
            });
        }
        else if (request.IsPrimary.HasValue)
        {
            existing.IsPrimary = request.IsPrimary.Value;
        }

        if (request.IsPrimary == true)
        {
            await _driverVehicles.SetSinglePrimaryVehicleAsync(userId, vehicle.Id, ct);
        }

        await _identityDbContext.SaveChangesAsync(ct);
        return Ok(new { success = true, message = "Vehicle assigned successfully", vehicleId = vehicle.Id });
    }

    [HttpPut("me/vehicles/{vehicleId:guid}/primary")]
    public async Task<IActionResult> SetPrimaryVehicle(Guid vehicleId, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var assignment = await _identityDbContext.DriverVehicleAssignments
            .FirstOrDefaultAsync(x => x.DriverId == userId && x.VehicleId == vehicleId, ct);
        if (assignment == null)
        {
            return NotFound(new { success = false, message = "Vehicle is not assigned to this driver" });
        }

        await _driverVehicles.SetSinglePrimaryVehicleAsync(userId, vehicleId, ct);
        await _identityDbContext.SaveChangesAsync(ct);
        return Ok(new { success = true, message = "Primary vehicle updated" });
    }

    [HttpDelete("me/vehicles/{vehicleId:guid}")]
    public async Task<IActionResult> RemoveMyVehicle(Guid vehicleId, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var assignment = await _identityDbContext.DriverVehicleAssignments
            .FirstOrDefaultAsync(x => x.DriverId == userId && x.VehicleId == vehicleId, ct);
        if (assignment == null)
        {
            return NotFound(new { success = false, message = "Vehicle is not assigned to this driver" });
        }

        var wasPrimary = assignment.IsPrimary;
        _identityDbContext.DriverVehicleAssignments.Remove(assignment);
        await _identityDbContext.SaveChangesAsync(ct);

        if (wasPrimary)
        {
            var next = await _identityDbContext.DriverVehicleAssignments
                .Where(x => x.DriverId == userId)
                .OrderByDescending(x => x.AssignedAt)
                .FirstOrDefaultAsync(ct);
            if (next != null)
            {
                next.IsPrimary = true;
                await _identityDbContext.SaveChangesAsync(ct);
            }
        }

        return Ok(new { success = true, message = "Vehicle removed from driver" });
    }

    /// <summary>
    /// Request OTP for password change
    /// </summary>
    [HttpPost("change-password/request-otp")]
    [Authorize]
    public async Task<IActionResult> RequestPasswordChangeOtp()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var outcome = await _mediator.Send(new RequestPasswordChangeOtpCommand(userId), HttpContext.RequestAborted);
        if (outcome.Status == AuthOutcomeStatus.NotFound) return NotFound(outcome.Message);
        return outcome.Success
            ? Ok(new { success = true, message = outcome.Message })
            : BadRequest(new { success = false, message = outcome.Message });
    }

    /// <summary>
    /// Change password with OTP verification
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var outcome = await _mediator.Send(
            new ChangePasswordCommand(userId, request.CurrentPassword, request.NewPassword, request.Otp),
            HttpContext.RequestAborted);
        if (outcome.Status == AuthOutcomeStatus.NotFound) return NotFound(outcome.Message);
        return outcome.Success
            ? Ok(new { success = true, message = outcome.Message })
            : BadRequest(new { success = false, message = outcome.Message });
    }

    /// <summary>
    /// Logout - blacklists the current token
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var jti = User.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;

        if (!string.IsNullOrEmpty(jti))
        {
            // Blacklist this specific token
            await _tokenBlacklistService.BlacklistTokenAsync(jti);
        }

        if (!string.IsNullOrEmpty(userId))
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user != null)
            {
                await _auditService.LogAsync(AuditActions.Logout, AuditCategories.Auth,
                    userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role);
            }
        }

        return Ok(new { success = true, message = "Logged out successfully" });
    }

    /// <summary>
    /// Logout from all devices - blacklists all tokens for the user
    /// </summary>
    [HttpPost("logout-all")]
    [Authorize]
    public async Task<IActionResult> LogoutAll()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        // Blacklist all access tokens for this user
        await _tokenBlacklistService.BlacklistUserTokensAsync(userId);
        
        // Revoke all refresh tokens for this user
        await _refreshTokenService.RevokeAllUserRefreshTokensAsync(userId);

        var user = await _userManager.FindByIdAsync(userId);
        if (user != null)
        {
            await _auditService.LogAsync(AuditActions.LogoutAll, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                details: "Logged out from all devices");
        }

        return Ok(new { success = true, message = "Logged out from all devices successfully" });
    }

    /// <summary>
    /// Refresh access token using refresh token (for backoffice)
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [RequestSizeLimit(MAX_AUTH_REQUEST_SIZE_BYTES)]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request)
    {
        // SECURITY: Input validation (OWASP Top 10 - A03:2021 Injection)
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return BadRequest(new { success = false, message = AuthMessages.RefreshTokenRequired });
        }
        
        var requestId = GetRequestId();
        var userAgent = GetUserAgent();

        // Validate the refresh token
        var refreshToken = await _refreshTokenService.ValidateRefreshTokenAsync(request.RefreshToken);
        if (refreshToken == null)
        {
            await _auditService.LogAsync(AuditActions.TokenRevoked, AuditCategories.Auth,
                isSuccess: false, errorMessage: "Invalid or expired refresh token",
                userAgent: userAgent, requestId: requestId);
            return Unauthorized(new { success = false, message = AuthMessages.InvalidOrExpiredToken });
        }

        // SECURITY: Device fingerprinting validation (OWASP Top 10 - A07:2021)
        // Validate device fingerprint matches stored fingerprint to detect token theft
        if (!string.IsNullOrWhiteSpace(request.DeviceFingerprint) && 
            !string.IsNullOrWhiteSpace(refreshToken.DeviceFingerprint) &&
            request.DeviceFingerprint != refreshToken.DeviceFingerprint)
        {
            // Device fingerprint mismatch - potential token theft
            await _refreshTokenService.RevokeRefreshTokenAsync(request.RefreshToken);
            await _auditService.LogAsync(AuditActions.TokenRevoked, AuditCategories.Auth,
                userId: refreshToken.UserId, isSuccess: false, 
                errorMessage: "Device fingerprint mismatch - potential token theft",
                userAgent: userAgent, requestId: requestId);
            return Unauthorized(new { success = false, message = AuthMessages.InvalidOrExpiredToken });
        }

        // Get the user
        var user = await _userManager.FindByIdAsync(refreshToken.UserId);
        if (user == null || !user.IsActive)
        {
            // Revoke the token if user doesn't exist or is inactive
            await _refreshTokenService.RevokeRefreshTokenAsync(request.RefreshToken);
            await _auditService.LogAsync(AuditActions.TokenRevoked, AuditCategories.Auth,
                userId: refreshToken.UserId, isSuccess: false, 
                errorMessage: "User not found or inactive",
                userAgent: userAgent, requestId: requestId);
            return Unauthorized(new { success = false, message = "User not found or inactive" });
        }

        // Allow refresh for any authenticated user (backoffice and driver app)
        var accessToken = await _tokenIssuer.IssueAsync(user, AuthTokenProfile.Refresh, HttpContext.RequestAborted);

        // Token rotation: Generate new refresh token and revoke old one
        // SECURITY: Include device fingerprinting for enhanced security (OWASP Top 10 - A07:2021)
        var newRefreshToken = await _refreshTokenService.GenerateRefreshTokenAsync(
            user.Id, 
            deviceId: request.DeviceId,
            deviceFingerprint: request.DeviceFingerprint);
        await _refreshTokenService.RevokeRefreshTokenAsync(request.RefreshToken, newRefreshToken.Token);

        _logger?.LogInformation("Token refreshed for user {UserId}", user.Id);

        return Ok(new RefreshTokenResponse(
            Token: accessToken.Token,
            Expiration: accessToken.ExpiresAt,
            RefreshToken: newRefreshToken.Token,
            RefreshTokenExpiration: newRefreshToken.ExpiresAt
        ));
    }

    /// <summary>
    /// Revoke a specific refresh token (for logout)
    /// </summary>
    [HttpPost("revoke")]
    [AllowAnonymous]
    public async Task<IActionResult> RevokeToken([FromBody] RefreshTokenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return BadRequest(new { success = false, message = "Refresh token is required" });
        }

        await _refreshTokenService.RevokeRefreshTokenAsync(request.RefreshToken);

        return Ok(new { success = true, message = "Token revoked successfully" });
    }

    /// <summary>
    /// Revoke all tokens for a specific user (admin action for security incidents)
    /// </summary>
    [HttpPost("revoke-user-tokens/{userId}")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> RevokeUserTokens(string userId)
    {
        var targetUser = await _userManager.FindByIdAsync(userId);
        if (targetUser == null)
            return NotFound(new { success = false, message = AuthMessages.UserNotFound });

        // Blacklist all tokens for this user
        await _tokenBlacklistService.BlacklistUserTokensAsync(userId);

        // Log the action
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var adminName = User.FindFirstValue("full_name");
        await _auditService.LogAsync(AuditActions.TokenRevoked, AuditCategories.Auth,
            userId: adminId, userName: adminName,
            entityId: userId, entityType: "User",
            details: $"Revoked all tokens for user: {targetUser.Email}");

        return Ok(new { success = true, message = $"All tokens revoked for {targetUser.Email}" });
    }

    // Roles a caller may request through the anonymous self-registration endpoints.
    // Privileged roles (SuperAdmin, Admin, Dispatcher) are created only via the
    // authenticated backoffice flow (create-backoffice-user). Compared case-insensitively
    // because ASP.NET Identity normalizes role names before lookup.

    /// <summary>
    /// Create a backoffice user (SuperAdmin or Admin). Only SuperAdmin can create accounts.
    /// </summary>
    [HttpPost("create-backoffice-user")]
    [Authorize(Policy = "Backoffice")]
    public async Task<IActionResult> CreateBackofficeUser([FromBody] CreateBackofficeUserRequest request)
    {
        // Cutover complete: admin accounts are managed by the back-office backend now.
        if (!_configuration.GetValue("Features:BackofficeLoginEnabled", false))
        {
            return NotFound(new { success = false, message = "Backoffice user management has moved to the back-office portal." });
        }

        // Validate role - only SuperAdmin and Admin are valid backoffice roles
        var validRoles = new[] { "SuperAdmin", "Admin" };
        if (!validRoles.Contains(request.Role))
        {
            return BadRequest(new { success = false, message = "Invalid role. Must be SuperAdmin or Admin." });
        }

        var existingUser = await _userManager.FindByEmailAsync(request.Email);
        if (existingUser != null)
            return BadRequest(new { success = false, message = "Email already registered" });

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            FullName = request.FullName,
            Role = request.Role,
            IsOnboarded = true,
            IsActive = true
        };

        var result = await _userManager.CreateAsync(user, request.Password);

        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            return BadRequest(new { success = false, message = errors });
        }

        // Add to role
        await _userManager.AddToRoleAsync(user, request.Role);

        // Log user creation
        var creatorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var creatorName = User.FindFirstValue("full_name");
        await _auditService.LogAsync(AuditActions.UserCreated, AuditCategories.User,
            userId: creatorId, userName: creatorName, 
            entityId: user.Id, entityType: "User",
            details: $"Created {request.Role}: {request.Email}");

        return Ok(new { success = true, message = $"{request.Role} created successfully" });
    }

    /// <summary>
    /// Verify email address with token
    /// </summary>
    [HttpPost("verify-email")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailRequest request)
    {
        var outcome = await _mediator.Send(new VerifyEmailCommand(request.Email, request.Token), HttpContext.RequestAborted);
        return outcome.Success
            ? Ok(new { success = true, message = outcome.Message })
            : BadRequest(new { success = false, message = outcome.Message });
    }

    /// <summary>
    /// Resend verification email
    /// </summary>
    [HttpPost("resend-verification")]
    [AllowAnonymous]
    public async Task<IActionResult> ResendVerificationEmail([FromBody] ResendVerificationRequest request)
    {
        var outcome = await _mediator.Send(new ResendVerificationEmailCommand(request.Email), HttpContext.RequestAborted);
        return outcome.Success
            ? Ok(new { success = true, message = outcome.Message })
            : BadRequest(new { success = false, message = outcome.Message });
    }

    /// <summary>
    /// Check registration status for an email
    /// Returns whether email is verified and registration is complete
    /// </summary>
    [HttpGet("registration-status")]
    [AllowAnonymous]
    public async Task<IActionResult> GetRegistrationStatus([FromQuery] string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest(new { success = false, message = AuthMessages.EmailRequired });
        }

        var user = await _userManager.FindByEmailAsync(email);
        if (user == null)
        {
            // Email doesn't exist - can proceed with registration
            return Ok(new
            {
                success = true,
                emailVerified = false,
                registrationComplete = false,
                canResume = false
            });
        }

        // Check registration status
        var emailVerified = user.EmailConfirmed;
        var livenessVerified = user.LivenessVerifiedAt != null;
        var registrationComplete = emailVerified && user.IsOnboarded;
        var canResume = emailVerified && !registrationComplete;

        return Ok(new
        {
            success = true,
            emailVerified,
            livenessVerified,
            registrationComplete,
            canResume
        });
    }

    /// <summary>
    /// Complete driver registration with documents (license and selfie)
    /// POST /api/auth/register/driver/complete
    /// Requires email to be verified first
    /// </summary>
    [HttpPost("register/driver/complete")]
    [AllowAnonymous]
    [RequestSizeLimit(21_000_000)] // 21MB max (2 files × 10MB + 1MB form overhead)
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> CompleteDriverRegistration(
        [FromForm] string email,
        IFormFile? licenseImage, // ASP.NET Core automatically binds IFormFile from form data
        IFormFile? selfieImage,  // No [FromForm] needed for IFormFile
        [FromForm] string? licenseNumber,
        [FromForm] string? licenseExpiryDate,
        [FromForm] string? address,
        CancellationToken ct)
    {
        // Phase-out: drivers are onboarded only through driver-application approval in the back-office.
        if (!_configuration.GetValue("Features:LegacyDriverOnboardingEnabled", false))
        {
            return NotFound(new { success = false, message = "This registration flow has been replaced. Please update the app and submit a driver application." });
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest(new { success = false, message = AuthMessages.EmailRequired });
        }

        var user = await _userManager.FindByEmailAsync(email);
        if (user == null)
        {
            return BadRequest(new { success = false, message = AuthMessages.UserNotFound });
        }

        // SECURITY: Verify at least one contact method is verified (email OR phone)
        if (!user.EmailConfirmed && !user.PhoneNumberConfirmed)
        {
            return BadRequest(new { success = false, message = "Email or phone number must be verified before completing registration" });
        }

        // SECURITY: Verify user is a Driver
        if (user.Role != UserRoles.Driver)
        {
            return BadRequest(new { success = false, message = "Only drivers can complete driver registration" });
        }

        // SECURITY: Verify registration is not already complete
        if (user.IsOnboarded)
        {
            return BadRequest(new { success = false, message = "Registration is already complete" });
        }

        // SECURITY: Require face liveness verification before submitting documents
        if (user.LivenessVerifiedAt == null)
        {
            return BadRequest(new { success = false, message = "Please complete face verification first" });
        }

        // Validate files if provided
        if (licenseImage != null && licenseImage.Length > 0)
        {
            if (licenseImage.Length > _maxFileSizeBytes)
            {
                var maxSizeMB = _maxFileSizeBytes / (1024.0 * 1024.0);
                return BadRequest(new { 
                    success = false, 
                    message = $"License image exceeds maximum size of {maxSizeMB:F1} MB" 
                });
            }
        }

        if (selfieImage != null && selfieImage.Length > 0)
        {
            if (selfieImage.Length > _maxFileSizeBytes)
            {
                var maxSizeMB = _maxFileSizeBytes / (1024.0 * 1024.0);
                return BadRequest(new { 
                    success = false, 
                    message = $"Selfie image exceeds maximum size of {maxSizeMB:F1} MB" 
                });
            }
        }

        try
        {
            string? licenseImageUrl = null;
            string? selfieImageUrl = null;

            // Process and upload license image if provided
            if (licenseImage != null && licenseImage.Length > 0 && _fileStorageService != null && _imageProcessor != null)
            {
                try
                {
                    // Process image: convert to WebP, resize, and optimize
                    await using var inputStream = licenseImage.OpenReadStream();
                    await using var processedStream = new MemoryStream();
                    
                    var processingResult = await _imageProcessor.ProcessImageAsync(inputStream, processedStream, ct);
                    
                    if (processingResult.Success)
                    {
                        // Generate secure filename: drivers/{userId}/license-{guid}.webp
                        var fileGuid = Guid.NewGuid().ToString("N");
                        var objectName = $"drivers/{user.Id}/license-{fileGuid}.webp";

                        // Upload to storage
                        processedStream.Position = 0;
                        licenseImageUrl = await _fileStorageService.UploadFileAsync(
                            processedStream,
                            "documents", // bucket name
                            objectName,
                            "image/webp",
                            ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error uploading license image for user {UserId}", user.Id);
                    // Continue without license image - user can upload later
                }
            }

            // Process and upload selfie image if provided
            if (selfieImage != null && selfieImage.Length > 0 && _fileStorageService != null && _imageProcessor != null)
            {
                try
                {
                    // Process image: convert to WebP, resize, and optimize
                    await using var inputStream = selfieImage.OpenReadStream();
                    await using var processedStream = new MemoryStream();
                    
                    var processingResult = await _imageProcessor.ProcessImageAsync(inputStream, processedStream, ct);
                    
                    if (processingResult.Success)
                    {
                        // Generate secure filename: drivers/{userId}/selfie-{guid}.webp
                        var fileGuid = Guid.NewGuid().ToString("N");
                        var objectName = $"drivers/{user.Id}/selfie-{fileGuid}.webp";

                        // Upload to storage
                        processedStream.Position = 0;
                        selfieImageUrl = await _fileStorageService.UploadFileAsync(
                            processedStream,
                            "documents", // bucket name
                            objectName,
                            "image/webp",
                            ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error uploading selfie image for user {UserId}", user.Id);
                    // Continue without selfie image - user can upload later
                }
            }

            // Mark user as onboarded (registration complete)
            user.IsOnboarded = true;
            var updateResult = await _userManager.UpdateAsync(user);

            if (!updateResult.Succeeded)
            {
                var errors = string.Join(", ", updateResult.Errors.Select(e => e.Description));
                _logger?.LogError("Failed to update user {UserId} after registration completion: {Errors}", user.Id, errors);
                return BadRequest(new { success = false, message = "Failed to complete registration" });
            }

            // Log registration completion
            await _auditService.LogAsync(AuditActions.UserCreated, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                details: "Driver registration completed with documents");

            return Ok(new { 
                success = true, 
                message = "Driver registration completed successfully",
                data = new
                {
                    licenseImageUrl,
                    selfieImageUrl,
                    licenseNumber,
                    licenseExpiryDate,
                    address
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error completing driver registration for {Email}", email);
            return StatusCode(500, new { 
                success = false, 
                message = "An error occurred while completing registration. Please try again." 
            });
        }
    }

    /// <summary>
    /// Get user's security questions for password reset (only shows questions they've answered)
    /// </summary>
    [HttpPost("forgot-password/get-questions")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSecurityQuestions([FromBody] ForgotPasswordRequest request)
    {
        var outcome = await _mediator.Send(new GetForgotPasswordQuestionsQuery(request.Email), HttpContext.RequestAborted);
        return outcome.Success
            ? Ok(new { success = true, questions = outcome.Questions.Select(q => new { number = q.Number, questionId = q.QuestionId, question = q.Question }) })
            : BadRequest(new { success = false, message = outcome.Message });
    }

    /// <summary>
    /// Request password reset (forgot password) - with security question verification
    /// </summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        var outcome = await _mediator.Send(
            new ForgotPasswordCommand(request.Email, ToAnswerInputs(request.SecurityAnswers)),
            HttpContext.RequestAborted);
        if (outcome.Status == AuthOutcomeStatus.NotFound) return NotFound(outcome.Message);
        return outcome.Success
            ? Ok(new { success = true, message = outcome.Message })
            : BadRequest(new { success = false, message = outcome.Message });
    }

    /// <summary>
    /// Reset password with token (web) or OTP (mobile)
    /// Supports both email link token and OTP code flows
    /// </summary>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        var outcome = await _mediator.Send(
            new ResetPasswordCommand(request.Email, request.Token, request.Otp, request.NewPassword, ToAnswerInputs(request.SecurityAnswers)),
            HttpContext.RequestAborted);
        if (outcome.Status == AuthOutcomeStatus.NotFound) return NotFound(outcome.Message);
        return outcome.Success
            ? Ok(new { success = true, message = outcome.Message })
            : BadRequest(new { success = false, message = outcome.Message });
    }

    /// <summary>
    /// Request password reset OTP for mobile apps (forgot password)
    /// Sends OTP code via email instead of reset link
    /// </summary>
    [HttpPost("forgot-password/mobile")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPasswordMobile([FromBody] ForgotPasswordRequest request)
    {
        var outcome = await _mediator.Send(new ForgotPasswordMobileCommand(request.Email), HttpContext.RequestAborted);
        if (outcome.Status == AuthOutcomeStatus.NotFound) return NotFound(outcome.Message);
        return outcome.Success
            ? Ok(new { success = true, message = outcome.Message })
            : BadRequest(new { success = false, message = outcome.Message });
    }

    /// <summary>
    /// DEPRECATED: Use /reset-password with Otp parameter instead
    /// Kept for backward compatibility - forwards to main reset-password endpoint
    /// </summary>
    [HttpPost("reset-password/mobile")]
    [AllowAnonymous]
    [Obsolete("Use /reset-password endpoint with Otp parameter instead")]
    public async Task<IActionResult> ResetPasswordMobile([FromBody] ResetPasswordMobileRequest request)
    {
        var outcome = await _mediator.Send(
            new ResetPasswordCommand(request.Email, null, request.Otp, request.NewPassword, ToAnswerInputs(request.SecurityAnswers)),
            HttpContext.RequestAborted);
        if (outcome.Status == AuthOutcomeStatus.NotFound) return NotFound(outcome.Message);
        return outcome.Success
            ? Ok(new { success = true, message = outcome.Message })
            : BadRequest(new { success = false, message = outcome.Message });
    }

    /// <summary>
    /// Get available security questions
    /// </summary>
    [HttpGet("security-questions")]
    [AllowAnonymous]
    public IActionResult GetSecurityQuestions()
    {
        var questions = SecurityQuestionsService.GetAllQuestions();
        return Ok(new { success = true, questions = questions.Select(q => new { id = q.Id, question = q.Question }) });
    }

    /// <summary>
    /// Check if email is available for registration
    /// Rate-limited to prevent email enumeration attacks
    /// </summary>
    [HttpPost("check-email")]
    [AllowAnonymous]
    public async Task<IActionResult> CheckEmailAvailability([FromBody] CheckEmailRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new { success = false, message = AuthMessages.EmailRequired });
        }

        // Normalize email (lowercase, trim)
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        // Basic email format validation
        if (!normalizedEmail.Contains('@') || !normalizedEmail.Contains('.'))
        {
            return Ok(new { success = true, available = false, message = "Invalid email format" });
        }

        var existingUser = await _userManager.FindByEmailAsync(normalizedEmail);
        
        if (existingUser != null)
        {
            // Email is already registered - user can only have one role
            return Ok(new { 
                success = true, 
                available = false, 
                message = $"This email is already registered as {existingUser.Role}. Each email can only be registered with one role.",
                existingRole = existingUser.Role
            });
        }

        return Ok(new { success = true, available = true, message = "Email is available" });
    }


}
