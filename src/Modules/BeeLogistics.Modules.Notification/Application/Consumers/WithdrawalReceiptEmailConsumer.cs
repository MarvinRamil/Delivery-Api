using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Application.Services;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Notification.Application.Consumers;

/// <summary>
/// Sends a withdrawal receipt email to the driver when a withdrawal is requested and DriverEmail is set.
/// </summary>
public class WithdrawalReceiptEmailConsumer : IConsumer<WithdrawalRequestedEvent>
{
    private readonly IEmailService _emailService;
    private readonly IEmailTemplateService _emailTemplateService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WithdrawalReceiptEmailConsumer> _logger;

    public WithdrawalReceiptEmailConsumer(
        IEmailService emailService,
        IEmailTemplateService emailTemplateService,
        IConfiguration configuration,
        ILogger<WithdrawalReceiptEmailConsumer> logger)
    {
        _emailService = emailService;
        _emailTemplateService = emailTemplateService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<WithdrawalRequestedEvent> context)
    {
        var msg = context.Message;
        if (string.IsNullOrWhiteSpace(msg.DriverEmail))
        {
            _logger.LogDebug("WithdrawalReceiptEmailConsumer: skipping - no DriverEmail for WithdrawalId {WithdrawalId}", msg.WithdrawalId);
            return;
        }

        try
        {
            var payoutUrl = (string?)null;
            var baseUrl = _configuration["Xendit:PayoutReceiptUrlFormat"] ?? _configuration["Xendit:DashboardBaseUrl"];
            if (!string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(msg.ProviderPayoutId))
                payoutUrl = $"{baseUrl.TrimEnd('/')}/{msg.ProviderPayoutId}";

            var email = _emailTemplateService.CreateWithdrawalReceiptEmail(
                to: msg.DriverEmail,
                driverName: msg.DriverName ?? msg.DriverEmail.Split('@')[0],
                amount: msg.Amount,
                requestedAt: msg.RequestedAtUtc,
                maskedAccountNumber: msg.MaskedAccountNumber ?? "****",
                bankName: msg.BankName ?? "",
                xenditPayoutId: msg.ProviderPayoutId,
                xenditPayoutUrl: payoutUrl);

            await _emailService.SendAsync(email, context.CancellationToken);
            _logger.LogInformation("Withdrawal receipt email queued for WithdrawalId {WithdrawalId} to {Email}", msg.WithdrawalId, msg.DriverEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send withdrawal receipt email for WithdrawalId {WithdrawalId} to {Email}", msg.WithdrawalId, msg.DriverEmail);
            throw;
        }
    }
}
