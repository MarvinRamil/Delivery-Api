using BeeLogistics.Modules.Identity.Application;
using BeeLogistics.Modules.Identity.Application.Services;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Application.Services;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Identity.Application.Auth.Password;

/// <summary>Send the OTP that authorises a password change for a signed-in user.</summary>
public record RequestPasswordChangeOtpCommand(string UserId) : IRequest<MessageOutcome>;

public class RequestPasswordChangeOtpCommandHandler : IRequestHandler<RequestPasswordChangeOtpCommand, MessageOutcome>
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly OtpService _otpService;
    private readonly IEmailService _emailService;
    private readonly ILogger<RequestPasswordChangeOtpCommandHandler>? _logger;

    public RequestPasswordChangeOtpCommandHandler(
        UserManager<ApplicationUser> userManager,
        OtpService otpService,
        IEmailService emailService,
        ILogger<RequestPasswordChangeOtpCommandHandler>? logger = null)
    {
        _userManager = userManager;
        _otpService = otpService;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task<MessageOutcome> Handle(RequestPasswordChangeOtpCommand request, CancellationToken ct)
    {
        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user == null) return MessageOutcome.NotFound(AuthMessages.UserNotFound);

        try
        {
            var otp = await _otpService.GenerateOtpAsync(user.Id, ct);

            // Send OTP via email (dynamic subject/body for password change)
            var otpEmailHtml = OtpService.CreateOtpEmailHtml(user.FullName, otp, OtpPurpose.PasswordChange);
            var otpEmail = new EmailMessage(
                To: user.Email!,
                Subject: OtpService.GetOtpEmailSubject(OtpPurpose.PasswordChange),
                Body: otpEmailHtml,
                IsHtml: true
            );

            await _emailService.SendAsync(otpEmail);
            _logger?.LogInformation("Password change OTP sent to {Email}", user.Email);

            return MessageOutcome.Ok("Verification code sent to your email. Please check your inbox.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send password change OTP to {Email}", user.Email);
            return MessageOutcome.Fail("Failed to send verification code. Please try again later.");
        }
    }
}

/// <summary>
/// Change a signed-in user's password, authorised by either an OTP or their current password.
/// </summary>
public record ChangePasswordCommand(
    string UserId,
    string? CurrentPassword,
    string? NewPassword,
    string? Otp) : IRequest<MessageOutcome>;

public class ChangePasswordCommandHandler : IRequestHandler<ChangePasswordCommand, MessageOutcome>
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly OtpService _otpService;
    private readonly IAuditService _auditService;
    private readonly ITokenBlacklistService _tokenBlacklistService;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IEmailService _emailService;
    private readonly IEmailTemplateService _emailTemplateService;
    private readonly ILogger<ChangePasswordCommandHandler>? _logger;

    public ChangePasswordCommandHandler(
        UserManager<ApplicationUser> userManager,
        OtpService otpService,
        IAuditService auditService,
        ITokenBlacklistService tokenBlacklistService,
        IRefreshTokenService refreshTokenService,
        IEmailService emailService,
        IEmailTemplateService emailTemplateService,
        ILogger<ChangePasswordCommandHandler>? logger = null)
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

    public async Task<MessageOutcome> Handle(ChangePasswordCommand request, CancellationToken ct)
    {
        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user == null) return MessageOutcome.NotFound(AuthMessages.UserNotFound);

        if (string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length < AuthHelpers.MinPasswordLength)
        {
            return MessageOutcome.Fail(AuthMessages.NewPasswordTooShort);
        }

        // Validate OTP if provided
        if (!string.IsNullOrWhiteSpace(request.Otp))
        {
            var otpResult = await _otpService.ValidateOtpAsync(user.Id, request.Otp, ct);
            if (!otpResult.IsValid)
            {
                await _auditService.LogAsync(AuditActions.PasswordChanged, AuditCategories.Auth,
                    userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                    isSuccess: false, errorMessage: $"OTP validation failed: {otpResult.Message}");
                return MessageOutcome.Fail(otpResult.Message);
            }
        }
        else
        {
            // If no OTP provided, require current password
            if (string.IsNullOrWhiteSpace(request.CurrentPassword))
            {
                return MessageOutcome.Fail("Either OTP or current password is required");
            }
        }

        IdentityResult result;
        if (!string.IsNullOrWhiteSpace(request.CurrentPassword))
        {
            // Use current password method
            result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        }
        else
        {
            // OTP validated, reset password directly (admin-like operation)
            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            result = await _userManager.ResetPasswordAsync(user, resetToken, request.NewPassword);
        }

        if (!result.Succeeded)
        {
            await _auditService.LogAsync(AuditActions.PasswordChanged, AuditCategories.Auth,
                userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role,
                isSuccess: false, errorMessage: "Password change failed");
            return MessageOutcome.Fail("Password change failed. Ensure current password is correct and new password meets requirements.");
        }

        // SECURITY: Invalidate all existing tokens after password change
        await _tokenBlacklistService.BlacklistUserTokensAsync(user.Id);
        await _refreshTokenService.RevokeAllUserRefreshTokensAsync(user.Id, ct);

        await _auditService.LogAsync(AuditActions.PasswordChanged, AuditCategories.Auth,
            userId: user.Id, userName: user.FullName, userEmail: user.Email, userRole: user.Role);

        // Send password change notification email
        try
        {
            var passwordChangedEmail = _emailTemplateService.CreatePasswordChangedEmail(
                user.Email!,
                user.FullName,
                DateTime.UtcNow
            );

            await _emailService.SendAsync(passwordChangedEmail);
            _logger?.LogInformation("Password change notification email sent to {Email}", user.Email);
        }
        catch (Exception ex)
        {
            // Log but don't fail password change
            _logger?.LogError(ex, "Failed to send password change notification email to {Email}", user.Email);
        }

        return MessageOutcome.Ok("Password changed successfully. Please login again.");
    }
}
