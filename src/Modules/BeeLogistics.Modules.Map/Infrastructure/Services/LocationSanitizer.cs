using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Map.Application.DTOs;
using BeeLogistics.Modules.Map.Application.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace BeeLogistics.Modules.Map.Infrastructure.Services;

/// <summary>
/// Comprehensive location sanitization service
/// Validates coordinates, speed, heading, timestamps, driver status, and detects anomalies
/// </summary>
public sealed class LocationSanitizer : ILocationSanitizer
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocationSanitizer> _logger;
    private readonly IDistanceCalculationService _distanceService;

    // Per-driver rate-limit budget and last-known position. Held by a singleton because this class
    // is scoped and gets a fresh instance for every message: while these lived here as fields they
    // were always empty on entry, so neither the rate limit nor the teleport check ever actually
    // ran in production.
    private readonly IDriverLocationStateTracker _stateTracker;

    // Configuration defaults
    private readonly int _maxSpeedKmh;
    private readonly int _maxCoordinatePrecision;
    private readonly int _maxUpdateRatePerSecond;
    private readonly int _updateBurstAllowance;
    private readonly int _maxLocationAgeMinutes;
    private readonly decimal _maxTeleportationDistanceKm;
    private readonly TimeSpan _baselineMaxAge;

    public LocationSanitizer(
        IServiceScopeFactory serviceScopeFactory,
        IConfiguration configuration,
        ILogger<LocationSanitizer> logger,
        IDistanceCalculationService distanceService,
        IDriverLocationStateTracker stateTracker)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _configuration = configuration;
        _logger = logger;
        _distanceService = distanceService;
        _stateTracker = stateTracker;

        // Load configuration with defaults
        _maxSpeedKmh = int.Parse(_configuration["Location:MaxSpeedKmh"] ?? "200");
        _maxCoordinatePrecision = int.Parse(_configuration["Location:MaxCoordinatePrecision"] ?? "6");
        _maxUpdateRatePerSecond = int.Parse(_configuration["Location:MaxUpdateRatePerSecond"] ?? "1");

        // Must stay above the driver app's BATCH_SIZE_LIMIT (10). It flushes its buffer with
        // Promise.all, so a normal MQTT flush arrives as one simultaneous burst; a lower allowance
        // would reject most of every flush the moment rate limiting became effective.
        _updateBurstAllowance = int.Parse(_configuration["Location:UpdateBurstAllowance"] ?? "15");

        _maxLocationAgeMinutes = int.Parse(_configuration["Location:MaxLocationAgeMinutes"] ?? "5");
        _maxTeleportationDistanceKm = decimal.Parse(_configuration["Location:MaxTeleportationDistanceKm"] ?? "100");

        // How long a last-known position stays usable as a teleport reference. Beyond this we have
        // no opinion rather than a stale one - see IDriverLocationStateTracker.GetBaseline.
        _baselineMaxAge = TimeSpan.FromMinutes(
            int.TryParse(_configuration["Location:BaselineMaxAgeMinutes"], out var baselineMinutes) && baselineMinutes > 0
                ? baselineMinutes
                : 5);
    }

    public async Task<SanitizationResult> SanitizeAsync(
        DriverLocationUpdateDto location,
        Guid driverId,
        bool isHistorical = false,
        CancellationToken ct = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // 1. Coordinate Validation
        if (!ValidateCoordinates(location, errors))
        {
            return new SanitizationResult
            {
                IsValid = false,
                Errors = errors,
                RejectionReason = SanitizationRejectionReason.InvalidCoordinates
            };
        }

        // 2. Round coordinates early to avoid precision validation issues
        // GPS devices often send 7-8 decimal places, which is valid GPS precision
        // We round to max precision here, then continue with validation
        // Create new record with rounded coordinates (records have init-only properties)
        location = location with
        {
            Latitude = Math.Round(location.Latitude, _maxCoordinatePrecision),
            Longitude = Math.Round(location.Longitude, _maxCoordinatePrecision)
        };

        // 3. Speed Validation
        if (!ValidateSpeed(location, errors, warnings))
        {
            return new SanitizationResult
            {
                IsValid = false,
                Errors = errors,
                Warnings = warnings,
                RejectionReason = SanitizationRejectionReason.InvalidSpeed
            };
        }

        // 4. Heading Validation
        if (!ValidateHeading(location, errors))
        {
            return new SanitizationResult
            {
                IsValid = false,
                Errors = errors,
                RejectionReason = SanitizationRejectionReason.InvalidHeading
            };
        }

        // 5. Timestamp Validation
        if (!ValidateTimestamp(location, isHistorical, errors))
        {
            return new SanitizationResult
            {
                IsValid = false,
                Errors = errors,
                RejectionReason = SanitizationRejectionReason.InvalidTimestamp
            };
        }

        // 6. Rate Limiting
        //    Skipped for historical points: the budget is measured against wall-clock "now", so a
        //    buffered backlog arriving in one burst would exhaust it and we would reject the very
        //    data this path exists to rescue.
        if (!isHistorical && !CheckRateLimit(driverId, errors))
        {
            return new SanitizationResult
            {
                IsValid = false,
                Errors = errors,
                RejectionReason = SanitizationRejectionReason.RateLimitExceeded
            };
        }

        // 7. Driver Validation
        var driverValidation = await ValidateDriverAsync(driverId, ct);
        if (!driverValidation.IsValid)
        {
            return new SanitizationResult
            {
                IsValid = false,
                Errors = driverValidation.Errors,
                RejectionReason = driverValidation.RejectionReason
            };
        }

        // 8. Anomaly Detection
        //    Live points only. The baseline is where the driver is *now*; measuring a point that
        //    was recorded half an hour ago against it would read as a jump backwards through space
        //    and reject legitimate backfill. Coordinate, speed, heading and driver checks above all
        //    still apply to historical points.
        if (!isHistorical)
        {
            var anomalyCheck = await CheckAnomaliesAsync(location, driverId, warnings, ct);
            if (!anomalyCheck.IsValid)
            {
                return new SanitizationResult
                {
                    IsValid = false,
                    Errors = anomalyCheck.Errors,
                    Warnings = warnings,
                    RejectionReason = SanitizationRejectionReason.AnomalyDetected
                };
            }
        }

        // 9. Sanitize values (normalize precision, clamp values)
        var sanitized = SanitizeValues(location);

        // Update tracking for next validation.
        //
        // Historical points are deliberately excluded: the baseline models where the driver is
        // *now*. Backfilling a 30-minute-old point into it would rewind that baseline and make the
        // next live point look like an impossible jump.
        if (!isHistorical)
        {
            _stateTracker.RecordAccepted(driverId, sanitized.Latitude, sanitized.Longitude, sanitized.Timestamp, DateTime.UtcNow);
        }

        return new SanitizationResult
        {
            IsValid = true,
            SanitizedLocation = sanitized,
            Warnings = warnings
        };
    }

    private bool ValidateCoordinates(DriverLocationUpdateDto location, List<string> errors)
    {
        // Check for null/zero coordinates
        if (location.Latitude == 0 && location.Longitude == 0)
        {
            errors.Add("Coordinates cannot be (0, 0)");
            return false;
        }

        // Check valid ranges
        if (location.Latitude < -90 || location.Latitude > 90)
        {
            errors.Add($"Latitude {location.Latitude} is out of valid range (-90 to 90)");
            return false;
        }

        if (location.Longitude < -180 || location.Longitude > 180)
        {
            errors.Add($"Longitude {location.Longitude} is out of valid range (-180 to 180)");
            return false;
        }

        // Note: decimal doesn't support NaN/Infinity, so no need to check
        // If coordinates were NaN, they would fail the range checks above

        return true;
    }

    private bool ValidatePrecision(DriverLocationUpdateDto location, List<string> errors)
    {
        // Check latitude precision
        var latStr = location.Latitude.ToString("G");
        var latDecimalPlaces = latStr.Contains('.') ? latStr.Split('.')[1].Length : 0;
        
        if (latDecimalPlaces > _maxCoordinatePrecision)
        {
            errors.Add($"Latitude precision ({latDecimalPlaces} decimals) exceeds maximum ({_maxCoordinatePrecision})");
            return false;
        }

        // Check longitude precision
        var lngStr = location.Longitude.ToString("G");
        var lngDecimalPlaces = lngStr.Contains('.') ? lngStr.Split('.')[1].Length : 0;
        
        if (lngDecimalPlaces > _maxCoordinatePrecision)
        {
            errors.Add($"Longitude precision ({lngDecimalPlaces} decimals) exceeds maximum ({_maxCoordinatePrecision})");
            return false;
        }

        return true;
    }

    private bool ValidateSpeed(DriverLocationUpdateDto location, List<string> errors, List<string> warnings)
    {
        if (!location.Speed.HasValue)
            return true; // Speed is optional

        var speed = location.Speed.Value;

        // Check for negative speed
        if (speed < 0)
        {
            errors.Add($"Speed cannot be negative: {speed} km/h");
            return false;
        }

        // Check maximum speed
        if (speed > _maxSpeedKmh)
        {
            errors.Add($"Speed {speed} km/h exceeds maximum allowed {_maxSpeedKmh} km/h");
            return false;
        }

        // Warning for very high speed (but still valid)
        if (speed > _maxSpeedKmh * 0.9m)
        {
            warnings.Add($"High speed detected: {speed} km/h");
        }

        return true;
    }

    private bool ValidateHeading(DriverLocationUpdateDto location, List<string> errors)
    {
        if (!location.Heading.HasValue)
            return true; // Heading is optional

        var heading = location.Heading.Value;

        // Heading should be 0-360 degrees
        if (heading < 0 || heading >= 360)
        {
            errors.Add($"Heading {heading} is out of valid range (0-360 degrees)");
            return false;
        }

        return true;
    }

    private bool ValidateTimestamp(DriverLocationUpdateDto location, bool isHistorical, List<string> errors)
    {
        var timestamp = location.Timestamp;
        var now = DateTime.UtcNow;

        // Check if timestamp is in the future.
        // Applies to historical points too - being backdated is the whole point of a backlog, but
        // no legitimate recorded fix is dated after the moment it was received.
        if (timestamp > now.AddSeconds(5)) // Allow 5 second clock skew
        {
            errors.Add($"Timestamp {timestamp} is in the future");
            return false;
        }

        // Check if timestamp is too old.
        // Not applied to historical points: they are backfilled precisely because the app could not
        // send them at the time, so they are older than this limit by definition.
        if (isHistorical)
            return true;

        var age = now - timestamp;
        if (age.TotalMinutes > _maxLocationAgeMinutes)
        {
            errors.Add($"Timestamp {timestamp} is too old (age: {age.TotalMinutes:F1} minutes, max: {_maxLocationAgeMinutes} minutes)");
            return false;
        }

        return true;
    }

    private bool CheckRateLimit(Guid driverId, List<string> errors)
    {
        // Token bucket, not a minimum interval between updates.
        //
        // The old minimum-interval form would have rejected legitimate traffic the moment it became
        // effective: the driver app publishes its whole buffer to MQTT through Promise.all, so up
        // to BATCH_SIZE_LIMIT points arrive within the same few milliseconds. A bucket absorbs that
        // burst and still caps a sustained flood at _maxUpdateRatePerSecond.
        if (_stateTracker.TryConsumeUpdateAllowance(driverId, DateTime.UtcNow, _maxUpdateRatePerSecond, _updateBurstAllowance))
            return true;

        errors.Add(
            $"Rate limit exceeded: more than {_updateBurstAllowance} updates queued for this driver " +
            $"(sustained limit {_maxUpdateRatePerSecond}/s)");
        return false;
    }

    private async Task<(bool IsValid, List<string> Errors, SanitizationRejectionReason? RejectionReason)> ValidateDriverAsync(
        Guid driverId,
        CancellationToken ct)
    {
        var errors = new List<string>();

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var user = await userManager.FindByIdAsync(driverId.ToString());
            
            if (user == null)
            {
                errors.Add($"Driver {driverId} not found");
                return (false, errors, SanitizationRejectionReason.DriverNotFound);
            }

            // Check if driver is active
            if (!user.IsActive)
            {
                errors.Add($"Driver {driverId} is not active");
                return (false, errors, SanitizationRejectionReason.DriverInactive);
            }

            // Check if user is a driver
            var isDriver = user.Role == "Driver";
            if (!isDriver)
            {
                errors.Add($"User {driverId} is not a driver");
                return (false, errors, SanitizationRejectionReason.DriverInactive);
            }

            // Note: We don't require IsOnline to be true here because location updates
            // might come in while driver is going offline, or we want to track location
            // even when offline for safety/analytics purposes.
            // If you want to enforce online status, uncomment below:
            /*
            if (!user.IsOnline)
            {
                errors.Add($"Driver {driverId} is not online");
                return (false, errors, SanitizationRejectionReason.DriverOffline);
            }
            */

            return (true, errors, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating driver {DriverId}", driverId);
            errors.Add($"Error validating driver: {ex.Message}");
            return (false, errors, SanitizationRejectionReason.DriverNotFound);
        }
    }

    private async Task<(bool IsValid, List<string> Errors)> CheckAnomaliesAsync(
        DriverLocationUpdateDto location,
        Guid driverId,
        List<string> warnings,
        CancellationToken ct)
    {
        var errors = new List<string>();

        // Only compare against a recent baseline. An expired one means we genuinely do not know
        // where the driver was, and guessing from a stale position is how a driver who went offline
        // and travelled gets stuck: the first point back fails the jump check, a rejected point
        // never becomes the new baseline, and every subsequent point fails the same way forever.
        var baseline = _stateTracker.GetBaseline(driverId, DateTime.UtcNow, _baselineMaxAge);
        if (baseline is null)
            return (true, errors);

        var last = baseline.Value;

        // Use PostGIS distance service for accurate calculation
        var distanceKm = await _distanceService.CalculateDistanceAsync(
            last.Latitude,
            last.Longitude,
            location.Latitude,
            location.Longitude,
            ct);

        var timeDiff = location.Timestamp - last.RecordedAtUtc;
        var timeDiffSeconds = Math.Max(timeDiff.TotalSeconds, 1); // Avoid division by zero

        // Calculate required speed to travel this distance
        var requiredSpeedKmh = (distanceKm / (decimal)timeDiffSeconds) * 3600;

        // Both conditions, not just distance. A long jump is only impossible if the elapsed time
        // makes it impossible - covering the case where the baseline is recent but the reported
        // timestamps are further apart than the arrival times suggest.
        if (distanceKm > _maxTeleportationDistanceKm && requiredSpeedKmh > _maxSpeedKmh)
        {
            errors.Add($"Impossible location jump detected: {distanceKm:F2} km in {timeDiffSeconds:F1} seconds ({requiredSpeedKmh:F0} km/h required, max {_maxSpeedKmh} km/h)");
            return (false, errors);
        }

        // Warning if speed seems too high but not impossible
        if (requiredSpeedKmh > _maxSpeedKmh && distanceKm > 10) // 10km threshold
        {
            warnings.Add($"Unusually high speed detected: {requiredSpeedKmh:F1} km/h required for {distanceKm:F2} km jump");
        }

        return (true, errors);
    }

    private DriverLocationUpdateDto SanitizeValues(DriverLocationUpdateDto location)
    {
        // Round coordinates to max precision
        var latRounded = Math.Round(location.Latitude, _maxCoordinatePrecision);
        var lngRounded = Math.Round(location.Longitude, _maxCoordinatePrecision);

        // Clamp speed if present
        decimal? speedClamped = location.Speed.HasValue
            ? Math.Max(0, Math.Min(location.Speed.Value, _maxSpeedKmh))
            : null;

        // Normalize heading to 0-360 range
        decimal? headingNormalized = location.Heading.HasValue
            ? ((location.Heading.Value % 360) + 360) % 360
            : null;

        return location with
        {
            Latitude = latRounded,
            Longitude = lngRounded,
            Speed = speedClamped,
            Heading = headingNormalized,
            Timestamp = location.Timestamp > DateTime.UtcNow.AddSeconds(5)
                ? DateTime.UtcNow // Clamp future timestamps to now
                : location.Timestamp
        };
    }

}

