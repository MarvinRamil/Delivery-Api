using BeeLogistics.Modules.Identity.Application.Services;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Identity.Application.Auth.Otp;

/// <summary>Resend the email OTP, invalidating the previous one.</summary>
public record ResendEmailOtpCommand(string? Email) : IRequest<OtpOutcome>;

public class ResendEmailOtpCommandHandler : IRequestHandler<ResendEmailOtpCommand, OtpOutcome>
{
    private const string GenericResentMessage =
        "If this email is valid, a new verification code has been sent. Please check your inbox.";

    private readonly OtpService _otpService;
    private readonly EmailOtpSender _emailOtpSender;
    private readonly ILogger<ResendEmailOtpCommandHandler>? _logger;

    public ResendEmailOtpCommandHandler(
        OtpService otpService,
        IEmailService emailService,
        ILogger<ResendEmailOtpCommandHandler>? logger = null)
    {
        _otpService = otpService;
        _emailOtpSender = new EmailOtpSender(emailService);
        _logger = logger;
    }

    public async Task<OtpOutcome> Handle(ResendEmailOtpCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return OtpOutcome.Fail(AuthMessages.EmailRequired);
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        // SECURITY: Check if email is locked
        if (await _otpService.IsEmailLockedAsync(normalizedEmail))
        {
            return OtpOutcome.Fail(AuthMessages.TooManyAttempts);
        }

        try
        {
            await _otpService.InvalidateEmailVerificationOtpAsync(normalizedEmail);
            var otp = await _otpService.GenerateEmailVerificationOtpAsync(normalizedEmail);
            await _emailOtpSender.SendAsync(normalizedEmail, otp);
            _logger?.LogInformation("OTP resent to {Email}", normalizedEmail);

            // SECURITY: Generic success message
            return OtpOutcome.Ok(GenericResentMessage);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to resend OTP to {Email}", normalizedEmail);
            // SECURITY: Generic message even on error
            return OtpOutcome.Ok(GenericResentMessage);
        }
    }
}
