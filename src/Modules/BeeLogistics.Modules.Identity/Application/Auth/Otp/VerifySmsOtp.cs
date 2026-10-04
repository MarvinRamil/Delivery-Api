using BeeLogistics.Modules.Identity.Application.Services;
using BeeLogistics.Modules.Identity.Application.Auth;
using MediatR;

namespace BeeLogistics.Modules.Identity.Application.Auth.Otp;

/// <summary>
/// Verify a phone OTP. Does not create an account - on success it marks the phone verified for
/// registration and returns a stateless registration token.
/// </summary>
public record VerifySmsOtpCommand(string? PhoneNumber, string? Otp) : IRequest<OtpOutcome>;

public class VerifySmsOtpCommandHandler : IRequestHandler<VerifySmsOtpCommand, OtpOutcome>
{
    private readonly OtpService _otpService;
    private readonly IRegistrationVerificationTokenService _registrationTokens;

    public VerifySmsOtpCommandHandler(
        OtpService otpService,
        IRegistrationVerificationTokenService registrationTokens)
    {
        _otpService = otpService;
        _registrationTokens = registrationTokens;
    }

    public async Task<OtpOutcome> Handle(VerifySmsOtpCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PhoneNumber) || string.IsNullOrWhiteSpace(request.Otp))
        {
            return OtpOutcome.Fail("Phone number and OTP are required");
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

        var otpResult = await _otpService.ValidatePhoneVerificationOtpAsync(normalizedPhone, request.Otp);
        if (!otpResult.IsValid)
        {
            return OtpOutcome.Fail(otpResult.Message);
        }

        await _otpService.SetPhoneVerifiedForRegistrationAsync(normalizedPhone);
        // Stateless token so Register-by-phone can trust OTP was done even when cache is not shared
        var registrationToken = _registrationTokens.Create(normalizedPhone);
        return OtpOutcome.Ok("Phone verified. You can now complete registration.", registrationToken);
    }
}
