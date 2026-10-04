using BeeLogistics.Modules.Map.Application.DTOs;
using BeeLogistics.Modules.Map.Application.Events;
using BeeLogistics.Modules.Map.Application.Services;
using BeeLogistics.Modules.Map.Domain;
using BeeLogistics.Modules.Map.Infrastructure.Services;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Map;

/// <summary>
/// The shared ingest pipeline (GitLab #37). Both transports go through here, so these assertions
/// cover MQTT and HTTP at once - which is the point of having extracted it.
/// </summary>
public class LocationIngestServiceTests
{
    private readonly ILocationSanitizer _sanitizer = Substitute.For<ILocationSanitizer>();
    private readonly IBus _bus = Substitute.For<IBus>();

    private static readonly Guid DriverId = Guid.NewGuid();

    private LocationIngestService Service() =>
        new(_sanitizer, _bus, NullLogger<LocationIngestService>.Instance);

    private static DriverLocationUpdateDto Point(DateTime? at = null) => new()
    {
        DriverId = DriverId,
        Latitude = 16.6159m,
        Longitude = 120.3166m,
        Speed = 40m,
        Heading = 90m,
        Accuracy = 12.5m,
        Timestamp = at ?? DateTime.UtcNow,
        DeviceId = "test-device"
    };

    private void SanitizerAccepts(DriverLocationUpdateDto sanitized) =>
        _sanitizer.SanitizeAsync(Arg.Any<DriverLocationUpdateDto>(), Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new SanitizationResult { IsValid = true, SanitizedLocation = sanitized });

    private void SanitizerRejects(SanitizationRejectionReason reason, params string[] errors) =>
        _sanitizer.SanitizeAsync(Arg.Any<DriverLocationUpdateDto>(), Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new SanitizationResult { IsValid = false, RejectionReason = reason, Errors = errors.ToList() });

    [Fact]
    public async Task Publishes_an_accepted_point_once()
    {
        var point = Point();
        SanitizerAccepts(point);

        var result = await Service().IngestAsync(DriverId, point, LocationSource.Mqtt);

        Assert.True(result.Accepted);
        await _bus.Received(1).Publish(Arg.Any<DriverLocationUpdatedEvent>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The HTTP path used to run no validation at all, so any authenticated caller could inject
    /// arbitrary coordinates. A rejection must stop before the publish, not merely be logged.
    /// </summary>
    [Fact]
    public async Task Rejected_point_is_not_published()
    {
        SanitizerRejects(SanitizationRejectionReason.InvalidCoordinates, "Coordinates cannot be (0, 0)");

        var result = await Service().IngestAsync(DriverId, Point(), LocationSource.Http);

        Assert.False(result.Accepted);
        Assert.Contains("Coordinates cannot be (0, 0)", result.RejectionReason);
        await _bus.DidNotReceive().Publish(Arg.Any<DriverLocationUpdatedEvent>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(LocationSource.Mqtt)]
    [InlineData(LocationSource.Http)]
    public async Task Stamps_the_transport_on_the_event(LocationSource source)
    {
        var point = Point();
        SanitizerAccepts(point);

        await Service().IngestAsync(DriverId, point, source);

        await _bus.Received(1).Publish(Arg.Is<DriverLocationUpdatedEvent>(e => e.Source == source), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The recorded time must survive ingest intact. Restamping it with "now" is what collapsed a
    /// whole buffered trail into a single instant.
    /// </summary>
    [Fact]
    public async Task Preserves_the_recorded_timestamp_and_accuracy()
    {
        var recordedAt = DateTime.UtcNow.AddMinutes(-27);
        var point = Point(recordedAt);
        SanitizerAccepts(point);

        await Service().IngestAsync(DriverId, point, LocationSource.Http, isHistorical: true);

        await _bus.Received(1).Publish(
            Arg.Is<DriverLocationUpdatedEvent>(e => e.Timestamp == recordedAt && e.Accuracy == 12.5m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Flags_historical_points_and_forwards_the_flag_to_the_sanitizer()
    {
        var point = Point(DateTime.UtcNow.AddMinutes(-27));
        SanitizerAccepts(point);

        await Service().IngestAsync(DriverId, point, LocationSource.Http, isHistorical: true);

        await _sanitizer.Received(1).SanitizeAsync(Arg.Any<DriverLocationUpdateDto>(), DriverId, true, Arg.Any<CancellationToken>());
        await _bus.Received(1).Publish(Arg.Is<DriverLocationUpdatedEvent>(e => e.IsHistorical), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Live ingest - the MQTT path - must keep publishing points that are not marked historical, so
    /// the consumer still runs them through Redis, the geo index and SignalR.
    /// </summary>
    [Fact]
    public async Task Live_points_are_not_flagged_historical()
    {
        var point = Point();
        SanitizerAccepts(point);

        await Service().IngestAsync(DriverId, point, LocationSource.Mqtt);

        await _sanitizer.Received(1).SanitizeAsync(Arg.Any<DriverLocationUpdateDto>(), DriverId, false, Arg.Any<CancellationToken>());
        await _bus.Received(1).Publish(Arg.Is<DriverLocationUpdatedEvent>(e => !e.IsHistorical), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The driver is the authenticated caller. A payload claiming a different driver must not be
    /// able to write into someone else's trail.
    /// </summary>
    [Fact]
    public async Task Uses_the_authenticated_driver_not_the_payload()
    {
        var spoofed = Point() with { DriverId = Guid.NewGuid() };
        SanitizerAccepts(spoofed);

        await Service().IngestAsync(DriverId, spoofed, LocationSource.Http);

        await _bus.Received(1).Publish(Arg.Is<DriverLocationUpdatedEvent>(e => e.DriverId == DriverId), Arg.Any<CancellationToken>());
    }
}
