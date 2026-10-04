using System.Text;
using System.Text.Json;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Integration.Application.Consumers;

/// <summary>
/// Forwards domain events to the back-office backend as HMAC-signed webhook
/// POSTs. Reliability comes from the existing MassTransit pipeline (EF outbox
/// on the publish side + delayed redelivery on consume faults) — a failed
/// delivery throws so MassTransit retries it; the receiver dedupes by event id.
/// </summary>
public class BackofficeWebhookConsumer :
    IConsumer<DriverApplicationSubmittedEvent>,
    IConsumer<DriverApplicationReviewedEvent>,
    IConsumer<DriverKycCompletedEvent>,
    IConsumer<GiveawayWinnerSelectedEvent>,
    IConsumer<MissionCompletedEvent>
{
    public const string HttpClientName = "backoffice-webhooks";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<BackofficeWebhookOptions> _options;
    private readonly ILogger<BackofficeWebhookConsumer> _logger;

    public BackofficeWebhookConsumer(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<BackofficeWebhookOptions> options,
        ILogger<BackofficeWebhookConsumer> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public Task Consume(ConsumeContext<DriverApplicationSubmittedEvent> context) =>
        DeliverAsync(context, "driver_application.submitted", context.Message);

    public Task Consume(ConsumeContext<DriverApplicationReviewedEvent> context) =>
        DeliverAsync(context, "driver_application.reviewed", context.Message);

    public Task Consume(ConsumeContext<DriverKycCompletedEvent> context) =>
        DeliverAsync(context, "driver_kyc.completed", context.Message);

    public Task Consume(ConsumeContext<GiveawayWinnerSelectedEvent> context) =>
        DeliverAsync(context, "giveaway.winner_selected", context.Message);

    public Task Consume(ConsumeContext<MissionCompletedEvent> context) =>
        DeliverAsync(context, "mission.completed", context.Message);

    private async Task DeliverAsync<T>(ConsumeContext context, string eventType, T data) where T : class
    {
        var options = _options.CurrentValue;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Endpoint))
        {
            return; // webhooks not configured — drop silently (feature off)
        }

        // MessageId is stable across MassTransit redeliveries → receiver-side dedupe key.
        var eventId = context.MessageId ?? Guid.NewGuid();
        var envelope = new
        {
            eventId,
            type = eventType,
            occurredAt = context.SentTime ?? DateTime.UtcNow,
            data,
        };

        var rawBody = JsonSerializer.Serialize(envelope, JsonOptions);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
        {
            Content = new StringContent(rawBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(
            BackofficeWebhookSigner.SignatureHeader,
            BackofficeWebhookSigner.Sign(options.Secret, timestamp, rawBody));
        request.Headers.TryAddWithoutValidation(BackofficeWebhookSigner.EventIdHeader, eventId.ToString());
        request.Headers.TryAddWithoutValidation(BackofficeWebhookSigner.EventTypeHeader, eventType);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, context.CancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(context.CancellationToken);
            _logger.LogWarning("Backoffice webhook {EventType} ({EventId}) rejected with {StatusCode}: {Body}",
                eventType, eventId, (int)response.StatusCode, body);
            // Throw so MassTransit's retry/delayed-redelivery pipeline kicks in.
            throw new HttpRequestException($"Backoffice webhook delivery failed with {(int)response.StatusCode}");
        }

        _logger.LogInformation("Backoffice webhook {EventType} ({EventId}) delivered", eventType, eventId);
    }
}
