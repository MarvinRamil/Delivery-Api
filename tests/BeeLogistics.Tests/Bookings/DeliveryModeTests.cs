using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// DispatchPriority is derived data, which is normally worth avoiding — it exists only because
/// enums are stored as strings here, so ordering on DeliveryMode directly would sort
/// alphabetically (OnDemand &lt; Pooling &lt; Regular) and be silently backwards.
///
/// The risk of derived data is drift, so these tests pin that there is no way to construct a
/// booking whose rank disagrees with its mode — including through the legacy constructor, where a
/// forgotten assignment would leave 0 rather than the Regular rank and sort every such booking
/// behind Pooling.
/// </summary>
public class DeliveryModeTests
{
    private static List<DeliveryStop> TwoStops() =>
    [
        new(Guid.Empty, 0, "Pickup", StopType.Pickup, 16.61m, 120.31m),
        new(Guid.Empty, 1, "Dropoff", StopType.Dropoff, 16.62m, 120.33m),
    ];

    private static Booking Build(DeliveryMode mode) => new(
        customerId: Guid.NewGuid(),
        vehicleType: "Motorcycle",
        cargoDescription: "Documents",
        scheduleDate: DateTime.UtcNow.AddHours(1),
        serviceType: ServiceType.Immediate,
        estimatedFare: 150m,
        stops: TwoStops(),
        deliveryMode: mode);

    [Theory]
    [InlineData(DeliveryMode.OnDemand, DeliveryModeRanking.OnDemand)]
    [InlineData(DeliveryMode.Regular, DeliveryModeRanking.Regular)]
    [InlineData(DeliveryMode.Pooling, DeliveryModeRanking.Pooling)]
    public void The_constructor_derives_the_rank_from_the_mode(DeliveryMode mode, int expectedRank)
    {
        var booking = Build(mode);

        Assert.Equal(mode, booking.DeliveryMode);
        Assert.Equal(expectedRank, booking.DispatchPriority);
    }

    [Fact]
    public void Omitting_the_mode_yields_Regular()
    {
        // Every caller that predates modes, and every client that sends no deliveryMode.
        var booking = new Booking(
            customerId: Guid.NewGuid(),
            vehicleType: "Motorcycle",
            cargoDescription: "Documents",
            scheduleDate: DateTime.UtcNow.AddHours(1),
            serviceType: ServiceType.Immediate,
            estimatedFare: 150m,
            stops: TwoStops());

        Assert.Equal(DeliveryMode.Regular, booking.DeliveryMode);
        Assert.Equal(DeliveryModeRanking.Regular, booking.DispatchPriority);
    }

    [Fact]
    public void Regular_is_ordinal_zero_so_a_default_value_is_the_safe_one()
    {
        Assert.Equal(DeliveryMode.Regular, default(DeliveryMode));
    }

    [Fact]
    public void OnDemand_outranks_Regular_which_outranks_Pooling()
    {
        // The whole point of the column. If this ever inverts, On-Demand customers pay a premium
        // to be served last.
        Assert.True(DeliveryModeRanking.OnDemand > DeliveryModeRanking.Regular);
        Assert.True(DeliveryModeRanking.Regular > DeliveryModeRanking.Pooling);
    }

    [Fact]
    public void Alphabetical_order_would_be_wrong_which_is_why_the_rank_is_an_int()
    {
        // Documents the trap: enums are persisted as strings, so ORDER BY "DeliveryMode" gives
        // OnDemand, Pooling, Regular — putting Regular last and Pooling ahead of it.
        var alphabetical = new[] { DeliveryMode.OnDemand, DeliveryMode.Pooling, DeliveryMode.Regular }
            .OrderBy(m => m.ToString(), StringComparer.Ordinal)
            .ToArray();
        var byRank = alphabetical
            .OrderByDescending(DeliveryModeRanking.DispatchPriorityOf)
            .ToArray();

        Assert.NotEqual(alphabetical, byRank);
        Assert.Equal(DeliveryMode.OnDemand, byRank[0]);
        Assert.Equal(DeliveryMode.Pooling, byRank[^1]);
    }

    [Fact]
    public void The_legacy_constructor_yields_the_Regular_rank_not_zero()
    {
        // 0 is not the Regular rank. Leaving DispatchPriority to default(int) here would sort every
        // legacy booking behind Pooling the moment dispatch ordering lands.
#pragma warning disable CS0618 // legacy ctor, deliberately exercised
        var booking = new Booking(
            Guid.NewGuid(), "Pickup St", "Dropoff Ave", "Motorcycle", "Documents",
            DateTime.UtcNow.AddHours(1));
#pragma warning restore CS0618

        Assert.Equal(DeliveryMode.Regular, booking.DeliveryMode);
        Assert.Equal(DeliveryModeRanking.Regular, booking.DispatchPriority);
        Assert.NotEqual(0, booking.DispatchPriority);
    }

    [Theory]
    [InlineData("Regular", DeliveryMode.Regular)]
    [InlineData("Pooling", DeliveryMode.Pooling)]
    [InlineData("OnDemand", DeliveryMode.OnDemand)]
    [InlineData("ondemand", DeliveryMode.OnDemand)]
    [InlineData("ONDEMAND", DeliveryMode.OnDemand)]
    [InlineData("oNdEmAnD", DeliveryMode.OnDemand)]
    public void TryParse_accepts_any_casing(string value, DeliveryMode expected)
    {
        Assert.True(DeliveryModePolicy.TryParse(value, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_treats_absent_as_Regular(string? value)
    {
        // Clients that know nothing about modes must not be rejected.
        Assert.True(DeliveryModePolicy.TryParse(value, out var mode));
        Assert.Equal(DeliveryMode.Regular, mode);
    }

    [Theory]
    [InlineData("7")]        // plain Enum.TryParse accepts this and yields an undefined value
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("999")]
    [InlineData("Express")]
    [InlineData("Priority")] // the name this mode used to have — must not silently work
    public void TryParse_rejects_values_that_are_not_modes(string value)
    {
        Assert.False(DeliveryModePolicy.TryParse(value, out _));
    }

    [Fact]
    public void Every_mode_has_a_rank()
    {
        // A new enum member with no rank would silently fall through to Regular. This fails when
        // someone adds a mode and forgets DeliveryModeRanking.
        foreach (var mode in Enum.GetValues<DeliveryMode>())
        {
            var rank = DeliveryModeRanking.DispatchPriorityOf(mode);
            Assert.True(rank > 0, $"{mode} has no positive rank");
        }

        var distinct = Enum.GetValues<DeliveryMode>()
            .Select(DeliveryModeRanking.DispatchPriorityOf)
            .Distinct()
            .Count();
        Assert.Equal(Enum.GetValues<DeliveryMode>().Length, distinct);
    }
}
