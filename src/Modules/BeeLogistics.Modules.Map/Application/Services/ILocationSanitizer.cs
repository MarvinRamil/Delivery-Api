using BeeLogistics.Modules.Map.Application.DTOs;

namespace BeeLogistics.Modules.Map.Application.Services;

/// <summary>
/// Service for sanitizing and validating location updates from MQTT
/// Performs comprehensive validation including coordinate ranges, speed limits,
/// anomaly detection, rate limiting, and driver validation
/// </summary>
public interface ILocationSanitizer
{
    /// <summary>
    /// Sanitizes and validates a location update
    /// </summary>
    /// <param name="location">Location update to validate</param>
    /// <param name="driverId">Authenticated driver ID (from the MQTT topic or the caller's JWT)</param>
    /// <param name="isHistorical">
    /// True when backfilling points the app buffered while offline. Relaxes exactly the two checks
    /// that are hostile to backdated data — the maximum-age limit and the per-driver rate limit —
    /// and leaves the live-state trackers untouched. Every other check still applies.
    /// </param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Sanitization result with validation status and sanitized location</returns>
    Task<SanitizationResult> SanitizeAsync(
        DriverLocationUpdateDto location,
        Guid driverId,
        bool isHistorical = false,
        CancellationToken ct = default);
}

/// <summary>
/// Result of location sanitization
/// </summary>
public sealed class SanitizationResult
{
    public bool IsValid { get; init; }
    public List<string> Errors { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public DriverLocationUpdateDto? SanitizedLocation { get; init; }
    public SanitizationRejectionReason? RejectionReason { get; init; }
}

/// <summary>
/// Reasons for rejecting a location update
/// </summary>
public enum SanitizationRejectionReason
{
    InvalidCoordinates,
    InvalidSpeed,
    InvalidHeading,
    InvalidTimestamp,
    DriverNotFound,
    DriverInactive,
    DriverOffline,
    RateLimitExceeded,
    AnomalyDetected,
    PrecisionExceeded
}

