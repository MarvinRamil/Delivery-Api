namespace BeeLogistics.Modules.Map.Domain;

/// <summary>
/// Which transport a location point arrived on.
///
/// Lives in Domain because it is persisted on <see cref="DriverLocationHistory"/> and also travels
/// on the wire in DriverLocationUpdatedEvent, so both the entity and the Application layer need it.
///
/// The practical use is answering "is the HTTP fallback actually firing?" without guessing:
/// <code>SELECT "Source", count(*) FROM map."DriverLocationHistory" GROUP BY "Source"</code>
/// </summary>
public enum LocationSource
{
    /// <summary>Published by the driver app to the MQTT broker and picked up by MqttLocationSubscriberService.</summary>
    Mqtt = 0,

    /// <summary>POSTed to /api/locations/update or /api/locations/batch, the fallback used when MQTT is unavailable (typically the app is backgrounded).</summary>
    Http = 1
}
