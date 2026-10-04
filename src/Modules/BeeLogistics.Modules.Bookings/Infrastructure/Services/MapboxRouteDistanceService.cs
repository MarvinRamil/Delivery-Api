using System.Globalization;
using System.Text.Json;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Bookings.Infrastructure.Services;

/// <summary>
/// Route distance from the Mapbox Directions API, falling back to straight-line Haversine.
/// </summary>
/// <remarks>
/// Fare is mostly distance, so distance is the input that matters most — and the previous
/// implementation was pure Haversine, which underestimates real road distance by roughly 20–40%
/// in a city. Every distance-based fare was systematically low.
///
/// Distance is deliberately computed here rather than accepted from the client. A client-supplied
/// <c>distanceKm</c> would be the same hole as a client-supplied fare: post 0.1 and ride for base
/// fare. Whatever a client computes for display is its own business.
///
/// Three properties make it safe to put a network call on the booking-creation path:
/// <list type="number">
/// <item><b>It never throws.</b> Timeout, non-200, malformed body, missing token — every path
/// falls back to Haversine × RoadFactor. A booking must not fail because a maps API is down.</item>
/// <item><b>It caches.</b> A booking is quoted and then created, so the identical route is
/// requested at least twice; without caching this would double the Mapbox bill for nothing.</item>
/// <item><b>It reports.</b> <see cref="BeeMetrics.RouteDistanceFallbacks"/> is what turns "our
/// fares look wrong" into a diagnosable question, since a silent fallback and a real answer are
/// otherwise indistinguishable in the fare.</item>
/// </list>
///
/// The pooling detour score in #67 deliberately keeps using Haversine directly: it ranks
/// candidate drivers on a 15-second tick, where a routing call per driver would be prohibitive.
/// Straight-line is fine for a <i>relative ranking</i>; it is not fine for a <i>fare</i>.
/// </remarks>
public class MapboxRouteDistanceService : IRouteDistanceCalculationService
{
    /// <summary>Multiplier applied to straight-line distance when falling back. Roads wander.</summary>
    public const decimal DefaultRoadFactor = 1.3m;

    public const int DefaultTimeoutSeconds = 4;
    public const int DefaultCacheMinutes = 60;

    /// <summary>
    /// Decimal places coordinates are rounded to for the cache key. Five is ~1 m, which is far
    /// finer than any fare band — it exists to make repeated quotes of "the same" route hit,
    /// not to blur distinct routes together.
    /// </summary>
    public const int DefaultCoordinatePrecision = 5;

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MapboxRouteDistanceService> _logger;
    private readonly RouteDistanceCalculationService _haversine = new();

    public MapboxRouteDistanceService(
        HttpClient http,
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<MapboxRouteDistanceService> logger)
    {
        _http = http;
        _cache = cache;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<decimal> CalculateRouteDistanceAsync(List<DeliveryStop> stops, CancellationToken ct = default)
    {
        if (stops == null || stops.Count < 2)
            return 0m;

        // Any stop without coordinates makes a real route impossible. The Haversine
        // implementation already has a defined behaviour for that case (5 km per stop); reuse it
        // rather than inventing a second guess.
        if (stops.Any(s => !s.Latitude.HasValue || !s.Longitude.HasValue))
            return await FallbackAsync(stops, "missing_coordinates", ct);

        var token = ResolveAccessToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            // Expected in local dev and in any environment where the Vault secret is absent.
            return await FallbackAsync(stops, "no_token", ct);
        }

        var cacheKey = BuildCacheKey(stops);
        if (_cache.TryGetValue<decimal>(cacheKey, out var cached))
        {
            BeeMetrics.RouteDistanceCacheHits.Add(1);
            return cached;
        }

        var timeout = TimeSpan.FromSeconds(
            _configuration.GetValue("Maps:Mapbox:RequestTimeoutSeconds", DefaultTimeoutSeconds));

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            var url = BuildUrl(stops, token);
            using var response = await _http.GetAsync(url, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[RouteDistance] Mapbox returned {StatusCode}; falling back to Haversine",
                    (int)response.StatusCode);
                return await FallbackAsync(stops, "http_" + (int)response.StatusCode, ct);
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            var meters = ReadFirstRouteDistanceMeters(await JsonDocument.ParseAsync(body, cancellationToken: timeoutCts.Token));

            if (meters is null)
            {
                _logger.LogWarning("[RouteDistance] Mapbox response had no usable route; falling back to Haversine");
                return await FallbackAsync(stops, "no_route", ct);
            }

            var km = Math.Round((decimal)(meters.Value / 1000d), 2, MidpointRounding.AwayFromZero);
            _cache.Set(cacheKey, km, TimeSpan.FromMinutes(
                _configuration.GetValue("Maps:Mapbox:CacheMinutes", DefaultCacheMinutes)));
            return km;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up, not us. Let that propagate rather than quietly pricing a
            // request nobody is waiting for.
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[RouteDistance] Mapbox timed out after {Timeout}s; falling back to Haversine",
                timeout.TotalSeconds);
            return await FallbackAsync(stops, "timeout", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RouteDistance] Mapbox call failed; falling back to Haversine");
            return await FallbackAsync(stops, "error", ct);
        }
    }

    /// <summary>
    /// The Mapbox token, from the Vault <c>driver-config</c> secret. Vault is registered as the
    /// highest-precedence configuration source at startup, so this resolves without any extra
    /// plumbing — and fails closed (Haversine) rather than throwing when the secret is absent.
    /// </summary>
    /// <remarks>
    /// Both key spellings are accepted for the same reason ConfigController accepts both: the
    /// secret has been written under either naming, and reading only one would degrade every fare
    /// to a straight line without any error to notice.
    /// </remarks>
    private string? ResolveAccessToken()
        => _configuration["DriverConfig:Maps:MapboxAccessToken"]
        ?? _configuration["Maps:MapboxAccessToken"];

    private async Task<decimal> FallbackAsync(List<DeliveryStop> stops, string reason, CancellationToken ct)
    {
        BeeMetrics.RouteDistanceFallbacks.Add(1, new KeyValuePair<string, object?>("reason", reason));

        var straightLine = await _haversine.CalculateRouteDistanceAsync(stops, ct);
        var roadFactor = _configuration.GetValue("Maps:Mapbox:RoadFactor", DefaultRoadFactor);
        return Math.Round(straightLine * roadFactor, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Mapbox wants <c>lon,lat</c> pairs — the opposite order to how they are stored and to how
    /// every other part of this codebase passes them. Getting this backwards yields a plausible
    /// wrong number rather than an error, so it is worth stating.
    /// </summary>
    private string BuildUrl(List<DeliveryStop> stops, string token)
    {
        var coordinates = string.Join(';', stops.Select(s =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.######},{1:0.######}",
                s.Longitude!.Value, s.Latitude!.Value)));

        return $"https://api.mapbox.com/directions/v5/mapbox/driving/{coordinates}" +
               $"?alternatives=false&geometries=geojson&overview=false&access_token={Uri.EscapeDataString(token)}";
    }

    private static double? ReadFirstRouteDistanceMeters(JsonDocument doc)
    {
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("routes", out var routes) ||
                routes.ValueKind != JsonValueKind.Array ||
                routes.GetArrayLength() == 0)
            {
                return null;
            }

            return routes[0].TryGetProperty("distance", out var distance) &&
                   distance.TryGetDouble(out var meters)
                ? meters
                : null;
        }
    }

    private string BuildCacheKey(List<DeliveryStop> stops)
    {
        var precision = _configuration.GetValue("Maps:Mapbox:CoordinatePrecision", DefaultCoordinatePrecision);
        var parts = stops.Select(s => string.Format(
            CultureInfo.InvariantCulture, "{0},{1}",
            Math.Round(s.Latitude!.Value, precision),
            Math.Round(s.Longitude!.Value, precision)));
        return "routedist:" + string.Join(';', parts);
    }
}
