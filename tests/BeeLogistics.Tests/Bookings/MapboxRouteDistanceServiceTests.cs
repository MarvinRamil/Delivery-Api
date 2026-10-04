using System.Net;
using System.Text;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Bookings.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// Distance now comes from a network call that sits on the booking-creation path, which is only
/// acceptable because it cannot fail the booking. Every test here is really the same assertion:
/// whatever Mapbox does, a number comes back.
///
/// The fallback also has to be visible — a straight-line answer and a real one are
/// indistinguishable in the resulting fare, so a silent fallback would turn "our fares look low"
/// into an unanswerable question.
/// </summary>
public class MapboxRouteDistanceServiceTests
{
    /// <summary>Canned HTTP, so no test touches the network. Counts calls to prove caching.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public int Calls { get; private set; }
        public string? LastUrl { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUrl = request.RequestUri?.ToString();
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static IConfiguration Config(bool withToken = true)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Maps:Mapbox:RoadFactor"] = "1.3",
            ["Maps:Mapbox:RequestTimeoutSeconds"] = "2",
        };
        if (withToken) settings["DriverConfig:Maps:MapboxAccessToken"] = "pk.test-token";
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static (MapboxRouteDistanceService Service, StubHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond, bool withToken = true)
    {
        var handler = new StubHandler(respond);
        var service = new MapboxRouteDistanceService(
            new HttpClient(handler),
            new MemoryCache(new MemoryCacheOptions()),
            Config(withToken),
            NullLogger<MapboxRouteDistanceService>.Instance);
        return (service, handler);
    }

    private static List<DeliveryStop> TwoStops(decimal? lat = 16.61m, decimal? lon = 120.31m) =>
    [
        new(Guid.Empty, 0, "Pickup", StopType.Pickup, lat, lon),
        new(Guid.Empty, 1, "Dropoff", StopType.Dropoff, 16.62m, 120.33m),
    ];

    [Fact]
    public async Task Uses_the_road_distance_Mapbox_returns()
    {
        var (service, _) = Build(_ => Json("""{"routes":[{"distance":8450.0}]}"""));

        var km = await service.CalculateRouteDistanceAsync(TwoStops());

        Assert.Equal(8.45m, km);
    }

    [Fact]
    public async Task Sends_coordinates_as_longitude_then_latitude()
    {
        // Mapbox wants lon,lat — the opposite order to how they are stored and passed everywhere
        // else here. Reversing them yields a plausible wrong distance rather than an error.
        var (service, handler) = Build(_ => Json("""{"routes":[{"distance":1000.0}]}"""));

        await service.CalculateRouteDistanceAsync(TwoStops());

        Assert.Contains("120.31,16.61", handler.LastUrl!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caches_so_a_quote_then_create_costs_one_call()
    {
        var (service, handler) = Build(_ => Json("""{"routes":[{"distance":5000.0}]}"""));

        var first = await service.CalculateRouteDistanceAsync(TwoStops());
        var second = await service.CalculateRouteDistanceAsync(TwoStops());

        Assert.Equal(first, second);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Falls_back_to_haversine_when_mapbox_errors()
    {
        var (service, _) = Build(_ => Json("upstream exploded", HttpStatusCode.InternalServerError));

        var km = await service.CalculateRouteDistanceAsync(TwoStops());

        // Straight line between these points is ~2.4 km; the road factor inflates it. The point
        // is that a usable number came back rather than an exception.
        Assert.True(km > 0m, "fallback must still produce a distance");
    }

    [Fact]
    public async Task Falls_back_when_the_response_has_no_route()
    {
        var (service, _) = Build(_ => Json("""{"routes":[]}"""));

        var km = await service.CalculateRouteDistanceAsync(TwoStops());

        Assert.True(km > 0m);
    }

    [Fact]
    public async Task Falls_back_when_the_body_is_not_json()
    {
        var (service, _) = Build(_ => Json("<html>gateway timeout</html>"));

        var km = await service.CalculateRouteDistanceAsync(TwoStops());

        Assert.True(km > 0m);
    }

    [Fact]
    public async Task Falls_back_without_calling_mapbox_when_no_token_is_configured()
    {
        // Normal in local dev and anywhere the Vault secret is absent. Must not be an error.
        var (service, handler) = Build(_ => Json("""{"routes":[{"distance":9000.0}]}"""), withToken: false);

        var km = await service.CalculateRouteDistanceAsync(TwoStops());

        Assert.Equal(0, handler.Calls);
        Assert.True(km > 0m);
    }

    [Theory]
    [InlineData("DriverConfig:Maps:MapboxAccessToken")]  // how the Vault driver-config secret spells it
    [InlineData("Maps:MapboxAccessToken")]               // the bare spelling ConfigController also accepts
    public async Task Reads_the_token_under_either_key_spelling(string key)
    {
        // Reading only one spelling would degrade every fare to a straight line with no error to
        // notice — the same reason ConfigController tolerates both.
        var handler = new StubHandler(_ => Json("""{"routes":[{"distance":6000.0}]}"""));
        var service = new MapboxRouteDistanceService(
            new HttpClient(handler),
            new MemoryCache(new MemoryCacheOptions()),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [key] = "pk.test-token",
            }).Build(),
            NullLogger<MapboxRouteDistanceService>.Instance);

        var km = await service.CalculateRouteDistanceAsync(TwoStops());

        Assert.Equal(1, handler.Calls);
        Assert.Equal(6.0m, km);
    }

    [Fact]
    public async Task Falls_back_without_calling_mapbox_when_a_stop_has_no_coordinates()
    {
        var (service, handler) = Build(_ => Json("""{"routes":[{"distance":9000.0}]}"""));

        var km = await service.CalculateRouteDistanceAsync(TwoStops(lat: null, lon: null));

        Assert.Equal(0, handler.Calls);
        Assert.True(km > 0m);
    }

    [Fact]
    public async Task The_road_factor_inflates_the_straight_line_fallback()
    {
        // Straight-line underestimates real road distance by 20-40% in a city, so the fallback
        // must not hand back a raw Haversine number and call it a fare input.
        var (withFactor, _) = Build(_ => Json("", HttpStatusCode.InternalServerError));
        var plain = await new MapboxRouteDistanceService(
            new HttpClient(new StubHandler(_ => Json("", HttpStatusCode.InternalServerError))),
            new MemoryCache(new MemoryCacheOptions()),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Maps:Mapbox:RoadFactor"] = "1.0",
            }).Build(),
            NullLogger<MapboxRouteDistanceService>.Instance)
            .CalculateRouteDistanceAsync(TwoStops());

        var inflated = await withFactor.CalculateRouteDistanceAsync(TwoStops());

        Assert.True(inflated > plain, $"expected {inflated} > {plain}");
    }

    [Fact]
    public async Task Fewer_than_two_stops_is_zero_not_a_call()
    {
        var (service, handler) = Build(_ => Json("""{"routes":[{"distance":9000.0}]}"""));

        var km = await service.CalculateRouteDistanceAsync(
            [new DeliveryStop(Guid.Empty, 0, "Pickup", StopType.Pickup, 16.61m, 120.31m)]);

        Assert.Equal(0m, km);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_rather_than_silently_falling_back()
    {
        // A cancelled request has no one waiting for it. Pricing it from Haversine would be work
        // done for nothing, and would hide the cancellation from the caller.
        var (service, _) = Build(_ => Json("""{"routes":[{"distance":5000.0}]}"""));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CalculateRouteDistanceAsync(TwoStops(), cts.Token));
    }
}
