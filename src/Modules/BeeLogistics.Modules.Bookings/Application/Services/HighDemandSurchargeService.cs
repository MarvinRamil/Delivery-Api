namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// Service for calculating high demand surcharges based on peak hours.
/// Follows Single Responsibility Principle (SRP) - only handles surcharge calculation.
/// </summary>
public class HighDemandSurchargeService : IHighDemandSurchargeService
{
    private readonly IPricingConfigurationService _configService;

    public HighDemandSurchargeService(IPricingConfigurationService configService)
    {
        _configService = configService;
    }

    public async Task<decimal> GetHighDemandMultiplierAsync(DateTime dateTime, CancellationToken ct = default)
    {
        var config = await _configService.GetHighDemandConfigAsync(ct);

        if (config.MaxMultiplier <= 1.0m || config.PeakHours.Length == 0)
            return 1.0m;

        var currentHour = dateTime.Hour;
        var currentMinute = dateTime.Minute;
        var currentTime = TimeSpan.FromHours(currentHour).Add(TimeSpan.FromMinutes(currentMinute));

        foreach (var peakHourRange in config.PeakHours)
        {
            if (TryParseTimeRange(peakHourRange, out var startTime, out var endTime))
            {
                if (IsTimeInRange(currentTime, startTime, endTime))
                {
                    // Calculate multiplier based on how busy it is (simplified)
                    // In production, use actual demand data
                    return config.MaxMultiplier;
                }
            }
        }

        return 1.0m;
    }

    private bool TryParseTimeRange(string range, out TimeSpan start, out TimeSpan end)
    {
        start = TimeSpan.Zero;
        end = TimeSpan.Zero;

        var parts = range.Split('-');
        if (parts.Length != 2)
            return false;

        if (TimeSpan.TryParse(parts[0], out start) && TimeSpan.TryParse(parts[1], out end))
            return true;

        return false;
    }

    private bool IsTimeInRange(TimeSpan time, TimeSpan start, TimeSpan end)
    {
        if (start <= end)
        {
            return time >= start && time <= end;
        }
        else
        {
            // Handles ranges that span midnight (e.g., 22:00-02:00)
            return time >= start || time <= end;
        }
    }
}
