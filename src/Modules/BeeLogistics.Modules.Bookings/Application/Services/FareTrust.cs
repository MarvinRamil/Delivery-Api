using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>What to do with a booking whose quoted fare disagrees with the recomputed one.</summary>
public enum FareTrustDecision
{
    /// <summary>Create the booking, charging the server's number.</summary>
    UseServerFare,

    /// <summary>Refuse and make the customer re-consent to the new price.</summary>
    RequoteRequired,
}

/// <summary>
/// Decides whether a client's quoted fare can still be honoured, now that the fare is recomputed
/// server-side at creation.
/// </summary>
/// <remarks>
/// Before this existed the create handler injected <c>IPricingService</c> and never called it: it
/// persisted the client's <c>EstimatedFare</c> verbatim, and built the cash-on-delivery payment
/// from the same number. A booking posted with <c>estimatedFare: 1</c> quoted the driver ~₱1 and,
/// for cash, was ₱1 they actually collected.
///
/// The fix is not simply "reject any mismatch". Two failure modes have to be traded off:
/// <list type="bullet">
/// <item>Rejecting every mismatch punishes honest customers. The high-demand surcharge reads
/// <c>UtcNow</c>, so tapping Confirm at 06:59:59 crosses into a peak window between quote and
/// submit; an admin editing a VehiclePricing row does the same.</item>
/// <item>Silently overwriting fixes the hole but can charge ₱312 for a ₱260 quote the customer
/// consented to.</item>
/// </list>
///
/// So the rule is asymmetric: the server fare is <b>always</b> the one persisted — that alone
/// closes the hole — and only a material <i>increase</i> needs re-consent. A client claiming a
/// fare higher than we compute is never rejected; we simply charge the lower number rather than
/// punish someone for our own price drop.
/// </remarks>
public static class FareTrust
{
    /// <summary>Absolute floor on tolerance, in pesos.</summary>
    public const decimal DefaultToleranceAbsolute = 25.00m;

    /// <summary>Proportional tolerance, so long expensive trips get proportional headroom.</summary>
    public const decimal DefaultToleranceRate = 0.10m;

    /// <summary>
    /// How far the server fare may exceed the quote before re-consent is needed: the larger of a
    /// flat floor and a percentage, so a ₱90 booking is not held to the same absolute drift as a
    /// ₱900 one.
    /// </summary>
    public static decimal Tolerance(decimal serverFare, IConfiguration configuration)
    {
        var absolute = configuration.GetValue("Pricing:FareTrust:ToleranceAbsolute", DefaultToleranceAbsolute);
        var rate = configuration.GetValue("Pricing:FareTrust:ToleranceRate", DefaultToleranceRate);
        var proportional = Math.Round(serverFare * rate, 2, MidpointRounding.AwayFromZero);
        return Math.Max(absolute, proportional);
    }

    /// <summary>Whether <c>Pricing:FareTrust:Enforce</c> is on. Ships off — see <see cref="Decide"/>.</summary>
    public static bool IsEnforced(IConfiguration configuration)
        => configuration.GetValue("Pricing:FareTrust:Enforce", false);

    /// <summary>
    /// The verdict. Callers persist the server fare in <i>every</i> case; this only decides
    /// whether the booking may proceed at all.
    /// </summary>
    /// <param name="quotedFare">What the client displayed to the customer. Zero or less means no
    /// quote was presented — <c>EstimatedFare</c> is a required positional member, so a payload
    /// omitting it deserialises to 0, and hard-failing that would break lenient clients.</param>
    /// <param name="serverFare">The recomputed fare.</param>
    /// <param name="tolerance">From <see cref="Tolerance"/>.</param>
    /// <param name="enforce">
    /// When false, a material increase is still reported by the caller's metric but the booking
    /// proceeds. This is what lets the security fix ship immediately while real drift data
    /// accumulates before the 400 goes live.
    /// </param>
    public static FareTrustDecision Decide(decimal quotedFare, decimal serverFare, decimal tolerance, bool enforce)
    {
        if (!enforce)
            return FareTrustDecision.UseServerFare;

        // No quote presented — nothing was shown to the customer, so nothing to re-consent to.
        if (quotedFare <= 0)
            return FareTrustDecision.UseServerFare;

        // Server cheaper, or within tolerance. Charging less than quoted never needs consent.
        var drift = serverFare - quotedFare;
        return drift > tolerance
            ? FareTrustDecision.RequoteRequired
            : FareTrustDecision.UseServerFare;
    }
}
