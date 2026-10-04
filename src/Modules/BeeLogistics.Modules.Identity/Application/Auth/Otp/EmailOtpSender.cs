using BeeLogistics.Modules.Identity.Application.Services;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Application.Services;

namespace BeeLogistics.Modules.Identity.Application.Auth.Otp;

/// <summary>
/// Builds and sends the account-creation OTP email. Shared by the send and resend handlers,
/// which composed the identical message inline before.
/// </summary>
internal sealed class EmailOtpSender
{
    private readonly IEmailService _emailService;

    public EmailOtpSender(IEmailService emailService)
    {
        _emailService = emailService;
    }

    public Task SendAsync(string normalizedEmail, string otp)
    {
        var otpEmailHtml = OtpService.CreateOtpEmailHtml(
            normalizedEmail.Split('@')[0], // Use email prefix as name (user not created yet)
            otp,
            OtpPurpose.EmailVerification
        );
        var otpEmail = new EmailMessage(
            To: normalizedEmail,
            Subject: OtpService.GetOtpEmailSubject(OtpPurpose.EmailVerification),
            Body: otpEmailHtml,
            IsHtml: true
        );

        return _emailService.SendAsync(otpEmail);
    }
}
