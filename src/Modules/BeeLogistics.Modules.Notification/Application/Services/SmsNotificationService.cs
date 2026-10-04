using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Domain;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Notification.Application.Services;

public class SmsNotificationService : ISmsNotificationService
{
    public const string PhoneVerificationOtpMessageType = "PhoneVerificationOtp";

    private readonly ISmsService _smsService;
    private readonly ISmsRecordRepository _smsRecordRepository;
    private readonly ILogger<SmsNotificationService> _logger;

    public SmsNotificationService(
        ISmsService smsService,
        ISmsRecordRepository smsRecordRepository,
        ILogger<SmsNotificationService> logger)
    {
        _smsService = smsService;
        _smsRecordRepository = smsRecordRepository;
        _logger = logger;
    }

    public async Task<bool> SendPhoneVerificationOtpAsync(
        string normalizedPhone,
        string otpCode,
        CancellationToken ct = default)
    {
        var message = $"Your MyBeeApp verification code is {otpCode}. It will expire in 10 minutes.";
        var sent = await _smsService.SendAsync(normalizedPhone, message, ct);

        try
        {
            var record = new SmsRecord
            {
                Id = Guid.NewGuid(),
                To = normalizedPhone,
                MessageType = PhoneVerificationOtpMessageType,
                Status = sent ? SmsStatus.Sent : SmsStatus.Failed,
                CreatedAt = DateTime.UtcNow,
                SentAt = sent ? DateTime.UtcNow : null,
                ErrorMessage = sent ? null : "SMS provider returned failure"
            };

            await _smsRecordRepository.AddAsync(record, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist SMS audit record for {Phone}", normalizedPhone);
        }

        return sent;
    }
}
