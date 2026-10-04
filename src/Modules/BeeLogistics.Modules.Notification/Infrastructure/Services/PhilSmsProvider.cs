using System.Net.Http.Json;
using System.Text.Json;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Notification.Infrastructure.Services;

/// <summary>
/// Fallback SMS provider — PhilSMS (dashboard.philsms.com).
/// </summary>
public class PhilSmsProvider : ISmsProvider
{
    public const string ProviderName = "PhilSMS";

    private readonly HttpClient _httpClient;
    private readonly ILogger<PhilSmsProvider> _logger;
    private readonly PhilSmsProviderOptions _options;

    public PhilSmsProvider(
        HttpClient httpClient,
        IConfiguration configuration,
        IOptions<SmsOptions> smsOptions,
        ILogger<PhilSmsProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = SmsConfigurationHelper.ResolvePhilSms(configuration, smsOptions.Value);

        if (string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            _logger.LogWarning("PhilSMS API token is not configured. PhilSMS fallback will be disabled.");
        }
    }

    public string Name => ProviderName;

    public async Task<SmsSendResult> SendAsync(string toPhoneNumber, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            _logger.LogWarning("Attempted PhilSMS to {Phone} but API token is not configured.", toPhoneNumber);
            return new SmsSendResult(false, ProviderName, null, "missing_api_token");
        }

        if (string.IsNullOrWhiteSpace(toPhoneNumber) || string.IsNullOrWhiteSpace(message))
        {
            _logger.LogWarning("Attempted PhilSMS with empty phone or message.");
            return new SmsSendResult(false, ProviderName, null, "validation");
        }

        var recipient = toPhoneNumber.TrimStart('+');
        var baseUrl = _options.ApiBaseUrl.TrimEnd('/');
        var requestUri = $"{baseUrl}/sms/send";

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiToken);
        request.Headers.Accept.ParseAdd("application/json");

        var payload = new
        {
            recipient,
            sender_id = _options.SenderId,
            type = "plain",
            message
        };

        request.Content = JsonContent.Create(payload, options: new JsonSerializerOptions
        {
            PropertyNamingPolicy = null
        });

        try
        {
            var response = await _httpClient.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "PhilSMS sent to {Phone}. Response: {Response}",
                    recipient,
                    Truncate(content, 500));
                return new SmsSendResult(true, ProviderName, content, null);
            }

            _logger.LogWarning(
                "PhilSMS send failed for {Phone}. Status: {StatusCode}. Response: {Response}",
                recipient,
                (int)response.StatusCode,
                Truncate(content, 1000));

            return new SmsSendResult(
                false,
                ProviderName,
                content,
                ((int)response.StatusCode).ToString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("PhilSMS send cancelled for {Phone}.", toPhoneNumber);
            return new SmsSendResult(false, ProviderName, null, "cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error sending SMS via PhilSMS to {Phone}.", toPhoneNumber);
            return new SmsSendResult(false, ProviderName, ex.Message, "exception");
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }
}
