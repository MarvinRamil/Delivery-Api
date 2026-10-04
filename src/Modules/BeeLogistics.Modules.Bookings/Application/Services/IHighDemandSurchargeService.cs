namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// Service for calculating high demand surcharges.
/// Follows Interface Segregation Principle (ISP) - single responsibility.
/// </summary>
public interface IHighDemandSurchargeService
{
    /// <summary>
    /// Calculates the high demand multiplier for a given date/time.
    /// Returns 1.0 if no surcharge applies.
    /// </summary>
    Task<decimal> GetHighDemandMultiplierAsync(DateTime dateTime, CancellationToken ct = default);
}
