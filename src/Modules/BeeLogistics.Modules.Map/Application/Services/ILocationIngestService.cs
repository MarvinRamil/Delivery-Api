using BeeLogistics.Modules.Map.Application.DTOs;
using BeeLogistics.Modules.Map.Domain;

namespace BeeLogistics.Modules.Map.Application.Services;

/// <summary>
/// The single ingest pipeline for driver locations, shared by every transport.
///
/// MqttLocationSubscriberService is not "the MQTT system" — it is a transport adapter that used to
/// own this pipeline inline. Strip the broker plumbing from it and what remains is:
///
///     DriverLocationUpdateDto -> ILocationSanitizer -> IBus.Publish(DriverLocationUpdatedEvent)
///
/// Only topic parsing is MQTT-specific, and DriverLocationUpdatedConsumer downstream was already
/// transport-agnostic. Extracting those steps here gives the HTTP endpoints the same validation
/// (coordinate ranges, speed caps, driver-is-actually-a-driver, teleport detection) that MQTT has
/// always had — they previously ran none of it — and keeps one publish site for the module.
///
/// Implementations must publish through IBus, never the scoped IPublishEndpoint. See
/// LocationIngestService for why; MapModuleOutboxConventionTests enforces it.
/// </summary>
public interface ILocationIngestService
{
    /// <param name="driverId">Authenticated driver, from the MQTT topic or the caller's JWT — never from the payload.</param>
    /// <param name="isHistorical">
    /// True for buffered points being backfilled after the app was offline. Relaxes the two
    /// sanitizer checks that are hostile to backdated data (age limit, rate limit) and routes the
    /// point to history only, leaving live state untouched.
    /// </param>
    Task<LocationIngestResult> IngestAsync(
        Guid driverId,
        DriverLocationUpdateDto location,
        LocationSource source,
        bool isHistorical = false,
        CancellationToken ct = default);
}

public sealed record LocationIngestResult(bool Accepted, string? RejectionReason = null)
{
    public static LocationIngestResult Ok() => new(true);

    public static LocationIngestResult Rejected(string reason) => new(false, reason);
}
