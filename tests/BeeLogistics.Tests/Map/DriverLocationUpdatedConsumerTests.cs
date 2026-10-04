using BeeLogistics.Modules.Map.Application.Consumers;
using BeeLogistics.Modules.Map.Application.Events;
using BeeLogistics.Modules.Map.Application.Interfaces;
using BeeLogistics.Modules.Map.Domain;
using BeeLogistics.Modules.Map.Infrastructure.Services;
using BeeLogistics.Modules.Map.Presentation.Hubs;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Map;

/// <summary>
/// Splits the consumer's two jobs apart (GitLab #37).
///
/// Live points must keep doing everything they always did - MQTT is the working path and these
/// tests exist to keep it that way. Historical points must do exactly one thing: land in history.
/// Running a buffered backlog through the live path would rewind the driver's current position,
/// hand offer matching a stale H3 cell, and walk the tracking customer's marker back through the
/// entire old trail.
/// </summary>
public class DriverLocationUpdatedConsumerTests
{
    private readonly IRedisLocationCache _redis = Substitute.For<IRedisLocationCache>();
    private readonly IDriverGeoIndex _geoIndex = Substitute.For<IDriverGeoIndex>();
    private readonly ILocationHistoryBatchWriter _history = Substitute.For<ILocationHistoryBatchWriter>();
    private readonly ILocationRepository _repository = Substitute.For<ILocationRepository>();
    private readonly IHubContext<LocationHub> _hub = Substitute.For<IHubContext<LocationHub>>();
    private readonly IClientProxy _clientProxy = Substitute.For<IClientProxy>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();

    private static readonly Guid DriverId = Guid.NewGuid();

    public DriverLocationUpdatedConsumerTests()
    {
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(_clientProxy);
        _hub.Clients.Returns(clients);

        // The consumer resolves UserManager inside a scope to sync ApplicationUser coordinates and
        // catches any failure. An empty provider makes that step a no-op without dragging Identity
        // into these tests.
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(Substitute.For<IServiceProvider>());
        _scopeFactory.CreateScope().Returns(scope);
    }

    private DriverLocationUpdatedConsumer Consumer() => new(
        _redis, _geoIndex, _history, _repository, _hub, _scopeFactory,
        NullLogger<DriverLocationUpdatedConsumer>.Instance);

    private static ConsumeContext<DriverLocationUpdatedEvent> ContextFor(DriverLocationUpdatedEvent msg)
    {
        var ctx = Substitute.For<ConsumeContext<DriverLocationUpdatedEvent>>();
        ctx.Message.Returns(msg);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private static DriverLocationUpdatedEvent Event(
        bool isHistorical = false,
        LocationSource source = LocationSource.Mqtt,
        DateTime? timestamp = null) => new()
        {
            DriverId = DriverId,
            Latitude = 16.6159m,
            Longitude = 120.3166m,
            Speed = 40m,
            Heading = 90m,
            Accuracy = 12.5m,
            Timestamp = timestamp ?? DateTime.UtcNow,
            DeviceId = "test-device",
            Source = source,
            IsHistorical = isHistorical
        };

    // ---- Live path: the MQTT behaviour that must not regress -------------------------------

    [Fact]
    public async Task Live_point_updates_redis_geo_index_snapshot_and_broadcasts()
    {
        await Consumer().Consume(ContextFor(Event()));

        await _redis.Received(1).SetDriverLocationAsync(DriverId, 16.6159m, 120.3166m, 40m, 90m, Arg.Any<CancellationToken>());
        await _geoIndex.Received(1).IndexDriverAsync(DriverId, 16.6159m, 120.3166m, Arg.Any<CancellationToken>());
        await _repository.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _history.Received(1).EnqueueAsync(Arg.Any<LocationHistoryEntry>());
        await _clientProxy.Received().SendCoreAsync("ReceiveLocationUpdate", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Live_point_still_drops_when_stale()
    {
        await Consumer().Consume(ContextFor(Event(timestamp: DateTime.UtcNow.AddMinutes(-5))));

        await _redis.DidNotReceive().SetDriverLocationAsync(
            Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>());
        await _history.DidNotReceive().EnqueueAsync(Arg.Any<LocationHistoryEntry>());
    }

    // ---- Historical path: history only ------------------------------------------------------

    [Fact]
    public async Task Historical_point_writes_history_and_nothing_else()
    {
        await Consumer().Consume(ContextFor(Event(isHistorical: true, source: LocationSource.Http, timestamp: DateTime.UtcNow.AddMinutes(-27))));

        await _history.Received(1).EnqueueAsync(Arg.Any<LocationHistoryEntry>());

        await _redis.DidNotReceive().SetDriverLocationAsync(
            Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>());
        await _geoIndex.DidNotReceive().IndexDriverAsync(Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _clientProxy.DidNotReceive().SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Being old is the normal condition for a backfilled point, so the staleness guard must not
    /// discard it. Before the fix this was moot: the HTTP path stamped every buffered point with
    /// the receive time, so nothing ever looked old enough to be caught.
    /// </summary>
    [Fact]
    public async Task Historical_point_survives_the_staleness_guard()
    {
        await Consumer().Consume(ContextFor(Event(isHistorical: true, source: LocationSource.Http, timestamp: DateTime.UtcNow.AddHours(-2))));

        await _history.Received(1).EnqueueAsync(Arg.Any<LocationHistoryEntry>());
    }

    [Fact]
    public async Task History_entry_carries_the_recorded_time_source_and_accuracy()
    {
        var recordedAt = DateTime.UtcNow.AddMinutes(-27);

        await Consumer().Consume(ContextFor(Event(isHistorical: true, source: LocationSource.Http, timestamp: recordedAt)));

        await _history.Received(1).EnqueueAsync(Arg.Is<LocationHistoryEntry>(e =>
            e.Timestamp == recordedAt &&
            e.Source == LocationSource.Http &&
            e.IsHistorical &&
            e.Accuracy == 12.5m));
    }

    [Fact]
    public async Task Live_history_entry_is_tagged_with_its_transport()
    {
        await Consumer().Consume(ContextFor(Event(source: LocationSource.Mqtt)));

        await _history.Received(1).EnqueueAsync(Arg.Is<LocationHistoryEntry>(e =>
            e.Source == LocationSource.Mqtt && !e.IsHistorical));
    }
}
