using System.Globalization;
using BeeLogistics.Modules.Bookings.Domain;
using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>The commercial knobs for one delivery mode.</summary>
/// <remarks>
/// These apply to <b>any</b> mode, not just On-Demand. The rate card is the Pooling price and the
/// faster tiers are markups on it — see <see cref="DeliveryModePricing"/>.
/// </remarks>
/// <param name="Multiplier">What the subtotal is multiplied by. 1.0 means no premium.</param>
/// <param name="FlatFee">Added on top of the multiplier, before clamping.</param>
/// <param name="MinFee">Floor on the premium, so a short trip still pays for the upgrade.</param>
/// <param name="MaxFee">Ceiling, so a long trip is not gouged. 0 means uncapped.</param>
/// <param name="DiscountRate">Fraction of the subtotal taken off. Retained for the case where a
/// mode must price <i>below</i> the rate card; unused now that Pooling <i>is</i> the rate card.</param>
/// <param name="MaxDiscount">Ceiling on the discount. 0 means uncapped.</param>
public readonly record struct ModePricing(
    decimal Multiplier,
    decimal FlatFee,
    decimal MinFee,
    decimal MaxFee,
    decimal DiscountRate,
    decimal MaxDiscount)
{
    /// <summary>No premium and no discount — what an unconfigured mode resolves to.</summary>
    public static readonly ModePricing None = new(1.0m, 0m, 0m, 0m, 0m, 0m);
}

/// <summary>
/// Turns a <see cref="DeliveryMode"/> and a fare subtotal into the premium or discount for it.
/// </summary>
/// <remarks>
/// Rates live in <c>Pricing:DeliveryModes</c> in appsettings rather than on <c>VehiclePricing</c>
/// rows. A mode multiplier is a global commercial knob, structurally identical to
/// <c>Pricing:HighDemandSurcharge:MaxMultiplier</c> — per-vehicle rows are for per-vehicle rates.
/// Putting it on the row would also silently lose it on the unknown-vehicle fallback path, where
/// there is no row to read.
///
/// Pure and static so it is cheap to test exhaustively, which matters because these are the
/// numbers that decide what customers are charged.
/// </remarks>
public static class DeliveryModePricing
{
    private const string Section = "Pricing:DeliveryModes";

    /// <summary>
    /// Rates for a mode, or <see cref="ModePricing.None"/> when unconfigured.
    /// </summary>
    /// <remarks>
    /// Missing config is deliberately a no-op rather than an exception or a default premium: a bad
    /// deploy should cost nothing, not hand out free deliveries or silently start charging extra.
    /// </remarks>
    public static ModePricing Read(IConfiguration configuration, DeliveryMode mode)
    {
        var section = configuration.GetSection($"{Section}:{mode}");
        if (!section.Exists())
            return ModePricing.None;

        return new ModePricing(
            Multiplier: Decimal(section, "Multiplier", 1.0m),
            FlatFee: Decimal(section, "FlatFee", 0m),
            MinFee: Decimal(section, "MinFee", 0m),
            MaxFee: Decimal(section, "MaxFee", 0m),
            DiscountRate: Decimal(section, "DiscountRate", 0m),
            MaxDiscount: Decimal(section, "MaxDiscount", 0m));
    }

    /// <summary>
    /// Reads one rate, falling back to <paramref name="fallback"/> when the value is missing,
    /// blank or unparseable.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>section.GetValue&lt;decimal&gt;</c>, which throws on a malformed value.
    /// This sits on the booking-creation path, so a single fat-fingered config entry would
    /// otherwise take out every booking request rather than merely disabling one mode's pricing.
    /// </remarks>
    private static decimal Decimal(IConfigurationSection section, string key, decimal fallback)
    {
        var raw = section[key];
        return string.IsNullOrWhiteSpace(raw)
            ? fallback
            : decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
    }

    /// <summary>
    /// The markup this mode adds to a subtotal: <c>subtotal × (Multiplier - 1) + FlatFee</c>,
    /// clamped to [MinFee, MaxFee] and rounded to 2dp. Returns 0 when the mode carries no markup.
    /// </summary>
    /// <remarks>
    /// Applies to <b>any</b> mode. The rate card is the Pooling price, so Pooling resolves to 0 and
    /// the faster tiers each add their own markup — which is why this is no longer called
    /// <c>OnDemandFee</c>.
    ///
    /// The <c>MinFee</c> floor only applies once there is a markup to speak of — otherwise an
    /// unconfigured mode would start charging <c>MinFee</c> out of nowhere.
    /// </remarks>
    public static decimal Premium(decimal subtotal, ModePricing rates)
    {
        if (subtotal <= 0)
            return 0m;

        var raw = (subtotal * (rates.Multiplier - 1.0m)) + rates.FlatFee;
        if (raw <= 0m)
            return 0m;

        if (raw < rates.MinFee)
            raw = rates.MinFee;

        if (rates.MaxFee > 0m && raw > rates.MaxFee)
            raw = rates.MaxFee;

        return Math.Round(raw, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The Pooling discount on a subtotal as a positive magnitude, capped by <c>MaxDiscount</c>
    /// and by the subtotal itself, rounded to 2dp.
    /// </summary>
    /// <remarks>
    /// Capped at the subtotal so no configuration can produce a negative fare, which would
    /// propagate into the driver's earnings quote and the cash amount they collect.
    /// </remarks>
    public static decimal PoolingDiscount(decimal subtotal, ModePricing rates)
    {
        if (subtotal <= 0 || rates.DiscountRate <= 0m)
            return 0m;

        var raw = subtotal * rates.DiscountRate;

        if (rates.MaxDiscount > 0m && raw > rates.MaxDiscount)
            raw = rates.MaxDiscount;

        if (raw > subtotal)
            raw = subtotal;

        return Math.Round(raw, 2, MidpointRounding.AwayFromZero);
    }
}
