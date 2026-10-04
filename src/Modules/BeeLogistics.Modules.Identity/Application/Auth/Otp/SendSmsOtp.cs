using BeeLogistics.Modules.Identity.Application.Services;
using BeeLogistics.Modules.Identity.Infrastructure;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Identity.Application.Auth;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Identity.Application.Auth.Otp;

/// <summary>Send a phone OTP via SMS for verification before account creation.</summary>
public record SendSmsOtpCommand(string? PhoneNumber) : IRequest<OtpOutcome>;

/// <summary>
/// SECURITY: generic responses to prevent phone enumeration, and provider failures are never
/// surfaced to the caller - only logged.
/// </summary>
public class SendSmsOtpCommandHandler : IRequestHandler<SendSmsOtpCommand, OtpOutcome>
{
    private const string GenericSentMessage = "If this phone number is valid, a verification code has been sent.";

    private readonly OtpService _otpService;
    private readonly IdentityAppDbContext _db;
    private readonly ISmsNotificationService _smsNotificationService;
    private readonly ILogger<SendSmsOtpCommandHandler>? _logger;

    public SendSmsOtpCommandHandler(
        OtpService otpService,
        IdentityAppDbContext db,
        ISmsNotificationService smsNotificationService,
        ILogger<SendSmsOtpCommandHandler>? logger = null)
    {
        _otpService = otpService;
        _db = db;
        _smsNotificationService = smsNotificationService;
        _logger = logger;
    }

    public async Task<OtpOutcome> Handle(SendSmsOtpCommand request, CancellationToken ct)
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

        // SECURITY: Check if phone is locked (too many failed attempts)
        if (await _otpService.IsPhoneLockedAsync(normalizedPhone))
        {
            return OtpOutcome.Fail(AuthMessages.TooManyAttempts);
        }

        // SECURITY: Check if phone already exists (but don't reveal this to prevent enumeration)
        var existingByPhone = await _db.Users
            .AsNoTracking()
            .AnyAsync(u => u.PhoneNumber == normalizedPhone, ct);
        if (existingByPhone)
        {
            _logger?.LogInformation("SMS OTP requested for existing phone: {Phone}", normalizedPhone);
            return OtpOutcome.Ok("If this phone number is registered, a verification code has been sent.");
        }

        try
        {
            var otp = await _otpService.GeneratePhoneVerificationOtpAsync(normalizedPhone);

            var sent = await _smsNotificationService.SendPhoneVerificationOtpAsync(normalizedPhone, otp, ct);
            if (!sent)
            {
                // SECURITY: Do not reveal provider failure details to the client
                _logger?.LogWarning("Failed to send SMS OTP to {Phone}", normalizedPhone);
            }

            // Always return generic success message (prevent enumeration / provider leakage)
            return OtpOutcome.Ok(GenericSentMessage);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error while sending SMS OTP to {Phone}", normalizedPhone);
            // SECURITY: Return generic message even on error
            return OtpOutcome.Ok(GenericSentMessage);
        }
    }
}
