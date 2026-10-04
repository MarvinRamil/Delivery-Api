namespace BeeLogistics.Modules.Notification.Application.Interfaces;

public interface ISmsNotificationService
{
    Task<bool> SendPhoneVerificationOtpAsync(string normalizedPhone, string otpCode, CancellationToken ct = default);
}
