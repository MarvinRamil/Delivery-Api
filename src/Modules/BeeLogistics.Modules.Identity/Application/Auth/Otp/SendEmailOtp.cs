using BeeLogistics.Modules.Identity.Application.Services;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Identity.Application.Auth.Otp;

/// <summary>Send an email OTP for verification before account creation.</summary>
public record SendEmailOtpCommand(string? Email) : IRequest<OtpOutcome>;

/// <summary>
/// SECURITY: generic responses throughout to prevent email enumeration - an address that already
/// exists, an address that does not, and a send failure are all answered identically.
/// </summary>
public class SendEmailOtpCommandHandler : IRequestHandler<SendEmailOtpCommand, OtpOutcome>
{
    private const string GenericSentMessage =
        "If this email is valid, a verification code has been sent. Please check your inbox.";

    private readonly OtpService _otpService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly EmailOtpSender _emailOtpSender;
    private readonly ILogger<SendEmailOtpCommandHandler>? _logger;

    public SendEmailOtpCommandHandler(
        OtpService otpService,
        UserManager<ApplicationUser> userManager,
        IEmailService emailService,
        ILogger<SendEmailOtpCommandHandler>? logger = null)
    {
        _otpService = otpService;
        _userManager = userManager;
        _emailOtpSender = new EmailOtpSender(emailService);
        _logger = logger;
    }

    public async Task<OtpOutcome> Handle(SendEmailOtpCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return OtpOutcome.Fail(AuthMessages.EmailRequired);
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        // SECURITY: Check if email is locked (too many failed attempts)
        if (await _otpService.IsEmailLockedAsync(normalizedEmail))
        {
            return OtpOutcome.Fail(AuthMessages.TooManyAttempts);
        }

        // SECURITY: Check if email already exists (but don't reveal this to prevent enumeration)
        var existingUser = await _userManager.FindByEmailAsync(normalizedEmail);
        if (existingUser != null)
        {
            _logger?.LogInformation("OTP requested for existing email: {Email}", normalizedEmail);
            return OtpOutcome.Ok("If this email is registered, a verification code has been sent.");
        }

        try
        {
            var otp = await _otpService.GenerateEmailVerificationOtpAsync(normalizedEmail);
            await _emailOtpSender.SendAsync(normalizedEmail, otp);
            _logger?.LogInformation("OTP sent to {Email}", normalizedEmail);

            return OtpOutcome.Ok(GenericSentMessage);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send OTP to {Email}", normalizedEmail);
            // SECURITY: Return generic message even on error (prevent enumeration)
            return OtpOutcome.Ok(GenericSentMessage);
        }
    }
}
