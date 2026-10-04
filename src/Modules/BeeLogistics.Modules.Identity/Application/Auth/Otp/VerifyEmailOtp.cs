using BeeLogistics.Modules.Identity.Application.Services;
using MediatR;

namespace BeeLogistics.Modules.Identity.Application.Auth.Otp;

/// <summary>
/// Verify an email OTP. Does not create an account - on success it marks the email verified for
/// registration and returns a stateless registration token.
/// </summary>
public record VerifyEmailOtpCommand(string? Email, string? Otp) : IRequest<OtpOutcome>;

public class VerifyEmailOtpCommandHandler : IRequestHandler<VerifyEmailOtpCommand, OtpOutcome>
{
    private readonly OtpService _otpService;
    private readonly IRegistrationVerificationTokenService _registrationTokens;

    public VerifyEmailOtpCommandHandler(
        OtpService otpService,
        IRegistrationVerificationTokenService registrationTokens)
    {
        _otpService = otpService;
        _registrationTokens = registrationTokens;
    }

    public async Task<OtpOutcome> Handle(VerifyEmailOtpCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Otp))
        {
            return OtpOutcome.Fail("Email and OTP are required");
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        if (await _otpService.IsEmailLockedAsync(normalizedEmail))
        {
            return OtpOutcome.Fail(AuthMessages.TooManyAttempts);
        }

        var otpResult = await _otpService.ValidateEmailVerificationOtpAsync(normalizedEmail, request.Otp);
        if (!otpResult.IsValid)
        {
            return OtpOutcome.Fail(otpResult.Message);
        }

        await _otpService.SetEmailVerifiedForRegistrationAsync(normalizedEmail);
        // Stateless token so Register can trust OTP was done even when cache is not shared (e.g. multiple API instances)
        var registrationToken = _registrationTokens.Create(normalizedEmail);
        return OtpOutcome.Ok("Email verified. You can now complete registration.", registrationToken);
    }
}
