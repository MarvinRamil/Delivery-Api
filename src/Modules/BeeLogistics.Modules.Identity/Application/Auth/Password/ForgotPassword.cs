using BeeLogistics.Modules.Identity.Application;
using BeeLogistics.Modules.Identity.Application.Services;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Application.Services;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using System.Text;

namespace BeeLogistics.Modules.Identity.Application.Auth.Password;

/// <summary>Email a password-reset link, gated on a security answer when the user has questions set.</summary>
public record ForgotPasswordCommand(string? Email, IReadOnlyList<SecurityAnswerInput>? SecurityAnswers) : IRequest<MessageOutcome>;

/// <summary>
/// SECURITY: unknown email, inactive account and wrong security answer all return the same
/// generic success message, so the endpoint cannot be used to enumerate accounts or brute-force
/// security answers.
/// </summary>
public class ForgotPasswordCommandHandler : IRequestHandler<ForgotPasswordCommand, MessageOutcome>
{
    private const string GenericSentMessage =
        "If the email exists and security question is answered correctly, a password reset link has been sent.";

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuditService _auditService;
    private readonly IEmailService _emailService;
    private readonly IEmailTemplateService _emailTemplateService;
    private readonly ILogger<ForgotPasswordCommandHandler>? _logger;

    public ForgotPasswordCommandHandler(
        UserManager<ApplicationUser> userManager,
        IAuditService auditService,
        IEmailService emailService,
        IEmailTemplateService emailTemplateService,
        ILogger<ForgotPasswordCommandHandler>? logger = null)
    {
        _userManager = userManager;
        _auditService = auditService;
        _emailService = emailService;
        _emailTemplateService = emailTemplateService;
        _logger = logger;
    }

    public async Task<MessageOutcome> Handle(ForgotPasswordCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return MessageOutcome.Fail(AuthMessages.EmailRequired);
        }

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user == null || !user.IsActive)
        {
            if (user == null)
            {
                _logger?.LogWarning("Password reset requested for unknown email {Email}", request.Email);
                await _auditService.LogAsync(AuditActions.PasswordResetRequested, AuditCategories.Auth,
                    userEmail: request.Email,
                    isSuccess: false, errorMessage: "Password reset requested for unknown email");
            }
            else
            {
                _logger?.LogWarning("Password reset requested for inactive account {UserId}", user.Id);
                await _auditService.LogAsync(AuditActions.PasswordResetRequested, AuditCategories.Auth,
                    userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                    isSuccess: false, errorMessage: "Password reset requested for inactive account");
            }
            // Don't reveal if email exists for security - return success anyway
            return MessageOutcome.Ok(GenericSentMessage);
        }

        // If user has security questions, verify at least one
        if (SecurityAnswerVerifier.HasSecurityQuestions(user))
        {
            if (request.SecurityAnswers == null || request.SecurityAnswers.Count == 0)
            {
                return MessageOutcome.Fail("At least one security answer is required");
            }

            if (!SecurityAnswerVerifier.AnyAnswerCorrect(user, request.SecurityAnswers))
            {
                await _auditService.LogAsync(AuditActions.PasswordResetRequested, AuditCategories.Auth,
                    userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                    isSuccess: false, errorMessage: "Incorrect security answer(s)");
                // Don't reveal if answer is wrong - return generic message
                return MessageOutcome.Ok(
                    "If the email exists and security questions are answered correctly, a password reset link has been sent.");
            }
        }

        try
        {
            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(resetToken));

            var resetEmail = _emailTemplateService.CreatePasswordResetEmail(
                user.Email!,
                user.FullName,
                encodedToken
            );

            await _emailService.SendAsync(resetEmail);
            _logger?.LogInformation("Password reset email sent to {Email}", user.Email);

            await _auditService.LogAsync(AuditActions.PasswordResetRequested, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role);

            return MessageOutcome.Ok(GenericSentMessage);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send password reset email to {Email}", request.Email);
            return MessageOutcome.Fail("Failed to send password reset email. Please try again later.");
        }
    }
}

/// <summary>Email a password-reset OTP (mobile flow) instead of a link.</summary>
public record ForgotPasswordMobileCommand(string? Email) : IRequest<MessageOutcome>;

/// <summary>
/// SECURITY: as with <see cref="ForgotPasswordCommandHandler"/>, unknown/inactive accounts and
/// send failures all produce the same generic success message. The one case that does surface an
/// error is the OTP lockout, which is unchanged from the original.
/// </summary>
public class ForgotPasswordMobileCommandHandler : IRequestHandler<ForgotPasswordMobileCommand, MessageOutcome>
{
    private const string GenericSentMessage = "If the email exists, a verification code has been sent.";

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly OtpService _otpService;
    private readonly IAuditService _auditService;
    private readonly IEmailService _emailService;
    private readonly ILogger<ForgotPasswordMobileCommandHandler>? _logger;

    public ForgotPasswordMobileCommandHandler(
        UserManager<ApplicationUser> userManager,
        OtpService otpService,
        IAuditService auditService,
        IEmailService emailService,
        ILogger<ForgotPasswordMobileCommandHandler>? logger = null)
    {
        _userManager = userManager;
        _otpService = otpService;
        _auditService = auditService;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task<MessageOutcome> Handle(ForgotPasswordMobileCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return MessageOutcome.Fail(AuthMessages.EmailRequired);
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _userManager.FindByEmailAsync(normalizedEmail);

        if (user == null || !user.IsActive)
        {
            if (user == null)
            {
                _logger?.LogWarning("Password reset OTP requested for unknown email {Email}", normalizedEmail);
                await _auditService.LogAsync(AuditActions.PasswordResetRequested, AuditCategories.Auth,
                    userEmail: normalizedEmail,
                    isSuccess: false, errorMessage: "Password reset OTP requested for unknown email");
            }
            else
            {
                _logger?.LogWarning("Password reset OTP requested for inactive account {UserId}", user.Id);
                await _auditService.LogAsync(AuditActions.PasswordResetRequested, AuditCategories.Auth,
                    userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                    isSuccess: false, errorMessage: "Password reset OTP requested for inactive account");
            }
            // Don't reveal if email exists for security - return success anyway
            return MessageOutcome.Ok(GenericSentMessage);
        }

        // SECURITY: Check if email is locked (too many failed attempts)
        if (await _otpService.IsEmailLockedAsync(normalizedEmail, ct))
        {
            _logger?.LogWarning("Password reset OTP requested for locked email {Email}", normalizedEmail);
            await _auditService.LogAsync(AuditActions.PasswordResetRequested, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                isSuccess: false, errorMessage: "OTP requests locked due to too many failed attempts");
            return MessageOutcome.Fail(AuthMessages.TooManyAttempts);
        }

        try
        {
            // Invalidate previous OTP if exists
            await _otpService.InvalidatePasswordResetOtpAsync(user.Email!, ct);

            // Generate OTP for password reset
            var otp = await _otpService.GeneratePasswordResetOtpAsync(user.Email!, ct);

            // Send OTP via email
            var otpEmailHtml = OtpService.CreateOtpEmailHtml(user.FullName, otp, OtpPurpose.PasswordReset);
            var otpEmail = new EmailMessage(
                To: user.Email!,
                Subject: OtpService.GetOtpEmailSubject(OtpPurpose.PasswordReset),
                Body: otpEmailHtml,
                IsHtml: true
            );

            await _emailService.SendAsync(otpEmail);
            _logger?.LogInformation("Password reset OTP sent to {Email}", user.Email);

            await _auditService.LogAsync(AuditActions.PasswordResetRequested, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role);

            // SECURITY: Always return generic success message (prevent email enumeration)
            return MessageOutcome.Ok(GenericSentMessage);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send password reset OTP to {Email}", normalizedEmail);
            await _auditService.LogAsync(AuditActions.PasswordResetRequested, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                isSuccess: false, errorMessage: $"Failed to send password reset OTP: {ex.Message}");
            // SECURITY: Return generic message even on error (prevent enumeration)
            return MessageOutcome.Ok(GenericSentMessage);
        }
    }
}
