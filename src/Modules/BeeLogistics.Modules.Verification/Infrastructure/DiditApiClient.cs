using System.Text;
using System.Text.Json;
using BeeLogistics.Modules.Verification.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Verification.Infrastructure;

public class DiditApiClient : IDiditApiClient
{
    private readonly HttpClient _http;
    private readonly DiditOptions _options;
    private readonly ILogger<DiditApiClient> _logger;

    public DiditApiClient(HttpClient http, IOptions<DiditOptions> options, ILogger<DiditApiClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", _options.ApiKey);
    }

    public bool Enabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.WorkflowId);

    public async Task<DiditSessionCreated> CreateSessionAsync(string vendorData, string? workflowId = null, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            throw new InvalidOperationException("Didit is disabled or not configured (Didit:Enabled/ApiKey/WorkflowId).");

        var effectiveWorkflowId = string.IsNullOrWhiteSpace(workflowId) ? _options.WorkflowId : workflowId;

        var body = JsonSerializer.Serialize(new
        {
            workflow_id = effectiveWorkflowId,
            vendor_data = vendorData,
            callback = _options.CallbackUrl
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("v3/session/", content, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[Didit] Create session failed HTTP {Status}: {Body}", (int)response.StatusCode, json);
            throw new HttpRequestException($"Didit create session failed: HTTP {(int)response.StatusCode}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var sessionId = root.GetProperty("session_id").GetString();
        var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(url))
            throw new HttpRequestException("Didit create session returned no session_id/url.");

        _logger.LogInformation("[Didit] Session created {SessionId} for vendorData={VendorData}", sessionId, vendorData);
        return new DiditSessionCreated(sessionId, url);
    }

    public async Task<JsonDocument?> GetDecisionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            return null;

        var response = await _http.GetAsync($"v3/session/{sessionId}/decision/", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[Didit] Get decision for {SessionId} returned HTTP {Status}", sessionId, (int)response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(json);
    }

    public async Task<Stream?> DownloadMediaAsync(string url, CancellationToken cancellationToken = default)
    {
        // Media URLs are presigned; must be absolute https. Guard against path-relative injection.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            _logger.LogWarning("[Didit] Refusing to download media from non-https/relative URL");
            return null;
        }

        var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[Didit] Media download failed HTTP {Status} from {Host}", (int)response.StatusCode, uri.Host);
            return null;
        }

        var ms = new MemoryStream();
        await response.Content.CopyToAsync(ms, cancellationToken);
        ms.Position = 0;
        return ms;
    }
}
