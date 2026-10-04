using System.Net.Http.Headers;
using System.Text.Json;
using BeeLogistics.Modules.Verification.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Verification.Infrastructure;

public class FaceMatchApiClient : IFaceMatchApiClient
{
    private readonly HttpClient _http;
    private readonly FaceMatchOptions _options;
    private readonly ILogger<FaceMatchApiClient> _logger;

    public FaceMatchApiClient(HttpClient http, IOptions<FaceMatchOptions> options, ILogger<FaceMatchApiClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public bool Enabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.BaseUrl);

    public async Task<float[]?> EmbedAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            return null;

        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(imageStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(streamContent, "file", "image.jpg");

        var response = await _http.PostAsync("v1/embed", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("embedding", out var emb) || emb.ValueKind != JsonValueKind.Array)
        {
            _logger.LogInformation("[FaceMatch] Embed: no face detected");
            return null;
        }
        return emb.EnumerateArray().Select(e => e.GetSingle()).ToArray();
    }

    public async Task<double?> VerifyAsync(Stream imageStream, float[] referenceEmbedding, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            return null;

        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(imageStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(streamContent, "file", "image.jpg");
        content.Add(new StringContent(JsonSerializer.Serialize(referenceEmbedding)), "reference_embedding");

        var response = await _http.PostAsync("v1/verify", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("match_score", out var score) || score.ValueKind != JsonValueKind.Number)
        {
            _logger.LogInformation("[FaceMatch] Verify: no face detected");
            return null;
        }
        return score.GetDouble();
    }
}
