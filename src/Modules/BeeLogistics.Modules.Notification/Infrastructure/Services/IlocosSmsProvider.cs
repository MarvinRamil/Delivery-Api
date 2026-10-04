using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Notification.Infrastructure.Services;

/// <summary>
/// Primary SMS provider — Ilocos relay at sms.ilocosscript.live.
/// </summary>
public class IlocosSmsProvider : ISmsProvider
{
    public const string ProviderName = "IlocosSms";

    private readonly HttpClient _httpClient;
    private readonly ILogger<IlocosSmsProvider> _logger;
    private readonly string _apiKey;

    public IlocosSmsProvider(
        HttpClient httpClient,
        IOptions<SmsOptions> options,
        ILogger<IlocosSmsProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = options.Value.Providers.IlocosSms.ApiKey;

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("Sms:Providers:IlocosSms:ApiKey is not configured. Ilocos SMS will fail until configured.");
        }
    }

    public string Name => ProviderName;

    public async Task<SmsSendResult> SendAsync(string toPhoneNumber, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("Attempted Ilocos SMS to {Phone} but API key is not configured.", toPhoneNumber);
            return new SmsSendResult(false, ProviderName, null, "missing_api_key", ShouldFailover: true);
        }

        if (string.IsNullOrWhiteSpace(toPhoneNumber) || string.IsNullOrWhiteSpace(message))
        {
            _logger.LogWarning("Attempted Ilocos SMS with empty phone or message.");
            return new SmsSendResult(false, ProviderName, null, "validation", ShouldFailover: false);
        }

        var e164 = toPhoneNumber.StartsWith('+') ? toPhoneNumber : $"+{toPhoneNumber}";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sms/send");
        request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
        request.Content = JsonContent.Create(new IlocosSmsRequest(e164, message));

        try
        {
            var response = await _httpClient.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                IlocosSmsResponse? body = null;
                try
                {
                    body = JsonSerializer.Deserialize<IlocosSmsResponse>(content);
                }
                catch
                {
                    // fall through with raw content logged
                }

                if (body?.Success == true)
                {
                    _logger.LogInformation(
                        "Ilocos SMS sent to {Phone}. Response: {Response}",
                        e164,
                        Truncate(content, 500));
                    return new SmsSendResult(true, ProviderName, body.To, null);
                }

                _logger.LogWarning(
                    "Ilocos SMS returned 200 but success=false for {Phone}. Response: {Response}",
                    e164,
                    Truncate(content, 1000));
                return new SmsSendResult(false, ProviderName, content, "success_false", ShouldFailover: true);
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                _logger.LogWarning(
                    "Ilocos SMS validation error for {Phone}. Response: {Response}",
                    e164,
                    Truncate(content, 1000));
                return new SmsSendResult(false, ProviderName, content, "400", ShouldFailover: false);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogError(
                    "Ilocos SMS unauthorized (check relay API key). Response: {Response}",
                    Truncate(content, 500));
                return new SmsSendResult(false, ProviderName, content, "401", ShouldFailover: true);
            }

            if (response.StatusCode == HttpStatusCode.BadGateway)
            {
                _logger.LogWarning(
                    "Ilocos SMS gateway unreachable for {Phone}. Response: {Response}",
                    e164,
                    Truncate(content, 1000));
                return new SmsSendResult(false, ProviderName, content, "502", ShouldFailover: true);
            }

            var shouldFailover = (int)response.StatusCode >= 500;
            _logger.LogWarning(
                "Ilocos SMS send failed for {Phone}. Status: {StatusCode}. Response: {Response}",
                e164,
                (int)response.StatusCode,
                Truncate(content, 1000));

            return new SmsSendResult(
                false,
                ProviderName,
                content,
                ((int)response.StatusCode).ToString(),
                ShouldFailover: shouldFailover);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("Ilocos SMS send cancelled for {Phone}.", toPhoneNumber);
            return new SmsSendResult(false, ProviderName, null, "cancelled", ShouldFailover: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error sending SMS via Ilocos relay to {Phone}.", toPhoneNumber);
            return new SmsSendResult(false, ProviderName, ex.Message, "exception", ShouldFailover: true);
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

    private sealed record IlocosSmsRequest(string To, string Message);

    private sealed class IlocosSmsResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("to")]
        public string? To { get; set; }
    }
}
