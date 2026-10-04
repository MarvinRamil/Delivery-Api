using BeeLogistics.Modules.Identity.Application.Services;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Identity.Application.Auth;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Identity.Application.Auth.Otp;

/// <summary>Resend the phone OTP, invalidating the previous one.</summary>
public record ResendSmsOtpCommand(string? PhoneNumber) : IRequest<OtpOutcome>;

public class ResendSmsOtpCommandHandler : IRequestHandler<ResendSmsOtpCommand, OtpOutcome>
{
    private const string GenericResentMessage = "If this phone number is valid, a new verification code has been sent.";

    private readonly OtpService _otpService;
    private readonly ISmsNotificationService _smsNotificationService;
    private readonly ILogger<ResendSmsOtpCommandHandler>? _logger;

    public ResendSmsOtpCommandHandler(
        OtpService otpService,
        ISmsNotificationService smsNotificationService,
        ILogger<ResendSmsOtpCommandHandler>? logger = null)
    {
        _otpService = otpService;
        _smsNotificationService = smsNotificationService;
        _logger = logger;
    }

    public async Task<OtpOutcome> Handle(ResendSmsOtpCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PhoneNumber))
        {
            return OtpOutcome.Fail(AuthMessages.PhoneRequired);
        }

        var normalizedPhone = AuthHelpers.NormalizePhoneNumber(request.PhoneNumber);
        if (!AuthHelpers.IsPlausiblePhoneNumber(normalizedPhone))
        {
            return OtpOutcome.Fail(AuthMessages.InvalidPhoneFormat);
        }

        if (await _otpService.IsPhoneLockedAsync(normalizedPhone))
        {
            return OtpOutcome.Fail(AuthMessages.TooManyAttempts);
        }

        try
        {
            await _otpService.InvalidatePhoneVerificationOtpAsync(normalizedPhone);
            var otp = await _otpService.GeneratePhoneVerificationOtpAsync(normalizedPhone);

            var sent = await _smsNotificationService.SendPhoneVerificationOtpAsync(normalizedPhone, otp, ct);
            if (!sent)
            {
                _logger?.LogWarning("Failed to resend SMS OTP to {Phone}", normalizedPhone);
            }

            return OtpOutcome.Ok(GenericResentMessage);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error while resending SMS OTP to {Phone}", normalizedPhone);
            return OtpOutcome.Ok(GenericResentMessage);
        }
    }
}
