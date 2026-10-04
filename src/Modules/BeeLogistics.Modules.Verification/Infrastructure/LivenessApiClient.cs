using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Verification.Infrastructure;

/// <summary>
/// Calls the YOLO liveness service (POST /v1/predict with image).
/// Response: { "faces": [ { "label": "real"|"fake", "confidence": number } ], "latency_ms": number }
/// </summary>
public interface ILivenessApiClient
{
    Task<bool> CheckLivenessAsync(Stream imageStream, CancellationToken cancellationToken = default);

    /// <summary>Check if the YOLO liveness service is configured and reachable (GET /v1/health).</summary>
    Task<(bool Configured, bool Reachable, string? Error)> CheckHealthAsync(CancellationToken cancellationToken = default);
}

public class LivenessApiClient : ILivenessApiClient
{
    private readonly HttpClient _http;
    private readonly LivenessOptions _options;
    private readonly ILogger<LivenessApiClient> _logger;

    public LivenessApiClient(HttpClient http, IOptions<LivenessOptions> options, ILogger<LivenessApiClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public async Task<bool> CheckLivenessAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning("[Liveness] YOLO liveness is disabled (Liveness:Enabled=false or BaseUrl not set). Skipping check.");
            return false;
        }

        _logger.LogInformation("[Liveness] Calling YOLO liveness service at {BaseUrl}v1/predict", _options.BaseUrl.TrimEnd('/') + "/");

        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(imageStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(streamContent, "file", "image.jpg");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync("v1/predict", content, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Liveness] YOLO service request failed: {Message}", ex.Message);
            throw;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        sw.Stop();

        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double? latencyMs = null;
        if (root.TryGetProperty("latency_ms", out var lat))
            latencyMs = lat.GetDouble();

        if (!root.TryGetProperty("faces", out var faces) || faces.GetArrayLength() == 0)
        {
            _logger.LogInformation("[Liveness] YOLO result: no faces detected (latency_ms={LatencyMs})", latencyMs ?? sw.ElapsedMilliseconds);
            return false;
        }

        // Consider live if at least one face is "real" with confidence above threshold
        foreach (var face in faces.EnumerateArray())
        {
            if (face.TryGetProperty("label", out var label) && label.GetString()?.Equals("real", StringComparison.OrdinalIgnoreCase) == true)
            {
                var confidence = 0.5;
                if (face.TryGetProperty("confidence", out var conf))
                    confidence = conf.GetDouble();
                if (confidence >= 0.5)
                {
                    _logger.LogInformation("[Liveness] YOLO result: real face, confidence={Confidence:F2} (latency_ms={LatencyMs})", confidence, latencyMs ?? sw.ElapsedMilliseconds);
                    return true;
                }
            }
        }

        _logger.LogInformation("[Liveness] YOLO result: no real face above threshold (latency_ms={LatencyMs})", latencyMs ?? sw.ElapsedMilliseconds);
        return false;
    }

    public async Task<(bool Configured, bool Reachable, string? Error)> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _logger.LogDebug("[Liveness] Health check: not configured (Enabled={Enabled}, BaseUrl set={HasBaseUrl})", _options.Enabled, !string.IsNullOrWhiteSpace(_options.BaseUrl));
            return (false, false, "Liveness disabled or BaseUrl not set");
        }

        try
        {
            var response = await _http.GetAsync("v1/health", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("[Liveness] Health check: YOLO service reachable at {BaseUrl}", _options.BaseUrl.TrimEnd('/'));
                return (true, true, null);
            }
            var error = $"HTTP {(int)response.StatusCode}";
            _logger.LogWarning("[Liveness] Health check: YOLO service returned {Error}", error);
            return (true, false, error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Liveness] Health check: failed to connect to YOLO service at {BaseUrl}", _options.BaseUrl.TrimEnd('/'));
            return (true, false, ex.Message);
        }
    }
}
