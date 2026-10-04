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

/// <summary>
/// Complete a password reset with either the emailed token (web) or an OTP (mobile).
/// </summary>
public record ResetPasswordCommand(
    string? Email,
    string? Token,
    string? Otp,
    string? NewPassword,
    IReadOnlyList<SecurityAnswerInput>? SecurityAnswers) : IRequest<MessageOutcome>;

public class ResetPasswordCommandHandler : IRequestHandler<ResetPasswordCommand, MessageOutcome>
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly OtpService _otpService;
    private readonly IAuditService _auditService;
    private readonly ITokenBlacklistService _tokenBlacklistService;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IEmailService _emailService;
    private readonly IEmailTemplateService _emailTemplateService;
    private readonly ILogger<ResetPasswordCommandHandler>? _logger;

    public ResetPasswordCommandHandler(
        UserManager<ApplicationUser> userManager,
        OtpService otpService,
        IAuditService auditService,
        ITokenBlacklistService tokenBlacklistService,
        IRefreshTokenService refreshTokenService,
        IEmailService emailService,
        IEmailTemplateService emailTemplateService,
        ILogger<ResetPasswordCommandHandler>? logger = null)
    {
        _userManager = userManager;
        _otpService = otpService;
        _auditService = auditService;
        _tokenBlacklistService = tokenBlacklistService;
        _refreshTokenService = refreshTokenService;
        _emailService = emailService;
        _emailTemplateService = emailTemplateService;
        _logger = logger;
    }

    public async Task<MessageOutcome> Handle(ResetPasswordCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return MessageOutcome.Fail("Email and new password are required");
        }

        // Must provide either token OR OTP
        if (string.IsNullOrWhiteSpace(request.Token) && string.IsNullOrWhiteSpace(request.Otp))
        {
            return MessageOutcome.Fail("Either token or OTP is required");
        }

        // SECURITY: Enhanced password validation (OWASP Top 10 - A07:2021)
        if (string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return MessageOutcome.Fail(AuthMessages.PasswordRequired);
        }
        if (request.NewPassword.Length < AuthHelpers.MinPasswordLength)
        {
            return MessageOutcome.Fail(AuthMessages.PasswordTooShort);
        }
        if (request.NewPassword.Length > AuthHelpers.MaxPasswordLength)
        {
            return MessageOutcome.Fail(AuthMessages.PasswordTooLong);
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _userManager.FindByEmailAsync(normalizedEmail);
        if (user == null)
        {
            return MessageOutcome.Fail("Invalid request");
        }

        if (!user.IsActive)
        {
            return MessageOutcome.Fail("Account is deactivated");
        }

        // Validate password complexity using Identity framework (after user is fetched)
        var passwordValidator = new PasswordValidator<ApplicationUser>();
        var validationResult = await passwordValidator.ValidateAsync(_userManager, user, request.NewPassword);
        if (!validationResult.Succeeded)
        {
            return MessageOutcome.Fail(AuthMessages.PasswordComplexityFailed);
        }

        // SECURITY: If user has security questions, they MUST be answered (for mobile OTP flow)
        if (SecurityAnswerVerifier.HasSecurityQuestions(user))
        {
            if (request.SecurityAnswers == null || request.SecurityAnswers.Count == 0)
            {
                await _auditService.LogAsync(AuditActions.PasswordReset, AuditCategories.Auth,
                    userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                    isSuccess: false, errorMessage: "Security answers required but not provided");
                return MessageOutcome.Fail("Security answers are required. Please answer at least one security question.");
            }

            if (!SecurityAnswerVerifier.AnyAnswerCorrect(user, request.SecurityAnswers))
            {
                await _auditService.LogAsync(AuditActions.PasswordReset, AuditCategories.Auth,
                    userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                    isSuccess: false, errorMessage: "Incorrect security answer(s)");
                return MessageOutcome.Fail("Invalid security answer(s). Please check your answers and try again.");
            }
        }

        try
        {
            IdentityResult result;

            // Handle OTP flow (mobile)
            if (!string.IsNullOrWhiteSpace(request.Otp))
            {
                var otpResult = await _otpService.ValidatePasswordResetOtpAsync(user.Email!, request.Otp, ct);
                if (!otpResult.IsValid)
                {
                    await _auditService.LogAsync(AuditActions.PasswordReset, AuditCategories.Auth,
                        userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                        isSuccess: false, errorMessage: $"OTP validation failed: {otpResult.Message}");
                    return MessageOutcome.Fail(otpResult.Message);
                }

                // OTP validated, generate reset token and reset password
                var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
                result = await _userManager.ResetPasswordAsync(user, resetToken, request.NewPassword);
            }
            // Handle token flow (web)
            else
            {
                var decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(request.Token!));
                result = await _userManager.ResetPasswordAsync(user, decodedToken, request.NewPassword);
            }

            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                _logger?.LogWarning("Password reset failed for {Email}: {Errors}", normalizedEmail, errors);
                await _auditService.LogAsync(AuditActions.PasswordReset, AuditCategories.Auth,
                    userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                    isSuccess: false, errorMessage: errors);
                return MessageOutcome.Fail("Invalid or expired reset token. Please request a new password reset.");
            }

            // SECURITY: Invalidate all existing tokens after password reset (shared logic)
            await _tokenBlacklistService.BlacklistUserTokensAsync(user.Id);
            await _refreshTokenService.RevokeAllUserRefreshTokensAsync(user.Id, ct);

            await _auditService.LogAsync(AuditActions.PasswordReset, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role);

            // Send password changed notification
            try
            {
                var passwordChangedEmail = _emailTemplateService.CreatePasswordChangedEmail(
                    user.Email!,
                    user.FullName,
                    DateTime.UtcNow
                );

                await _emailService.SendAsync(passwordChangedEmail);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to send password changed notification to {Email}", user.Email);
                // Don't fail reset if notification email fails
            }

            return MessageOutcome.Ok("Password reset successfully. Please login with your new password.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error resetting password for {Email}", normalizedEmail);
            return MessageOutcome.Fail("Password reset failed. Please try again.");
        }
    }
}
