using BeeLogistics.Modules.Bookings.Domain;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// Everything a fare depends on.
/// </summary>
/// <remarks>
/// Deliberately has no priorityFee. The premium is derived here from
/// <see cref="DeliveryMode"/>, never supplied by the caller — a caller-supplied premium was the
/// shape of the fare hole closed in #68. Replacing the old parameter list with a record made that
/// removal a compile error at every call site rather than a silently ignored argument.
/// </remarks>
public sealed record FareRequest(
    string VehicleType,
    List<DeliveryStop> Stops,
    decimal? WeightKg = null,
    DateTime? ScheduledDateTime = null,
    DeliveryMode DeliveryMode = DeliveryMode.Regular);

public interface IPricingService
{
    Task<PricingResult> CalculateFareAsync(FareRequest request, CancellationToken ct = default);
}

public class PricingResult
{
    public decimal BaseFare { get; set; }
    public decimal DistanceFare { get; set; }
    public decimal WeightSurcharge { get; set; }
    /// <summary>
    /// The On-Demand premium. Kept under this name because it is plumbed end-to-end through the
    /// domain, the column, the breakdown and the driver's earnings quote; renaming it to
    /// OnDemandFee is a wide mechanical change best not mixed into a pricing change.
    /// </summary>
    public decimal PriorityFee { get; set; }

    /// <summary>The Pooling discount, as a positive magnitude subtracted from the total.</summary>
    public decimal PoolingDiscount { get; set; }

    /// <summary>Echoed back so a quote states which mode it priced.</summary>
    public DeliveryMode DeliveryMode { get; set; }
    public decimal HighDemandSurcharge { get; set; }
    public decimal TollFee { get; set; }
    public decimal TotalFare { get; set; }
    public decimal DistanceKm { get; set; }
    public string? Breakdown { get; set; }
}
