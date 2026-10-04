using BeeLogistics.Modules.Bookings.Domain;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// Parses the delivery mode off the wire.
/// </summary>
/// <remarks>
/// A pure static, like <c>OfferExpiry</c> and <c>DriverCapacityPolicy</c> — the shape this test
/// suite reaches most easily, and the reason both of those are well covered.
///
/// The mode-to-rank mapping deliberately lives in <see cref="DeliveryModeRanking"/> instead:
/// Booking's constructor needs it, and Domain must not reference Application.
/// </remarks>
public static class DeliveryModePolicy
{
    /// <summary>
    /// Parses the wire value. Absent, null or blank means <see cref="DeliveryMode.Regular"/>, so
    /// senders that know nothing about modes keep working unchanged.
    /// </summary>
    /// <remarks>
    /// Two guards beyond a plain <c>Enum.TryParse</c>, which the existing
    /// <c>Enum.TryParse&lt;ServiceType&gt;</c> in the create handler lacks:
    /// <list type="bullet">
    /// <item><b>Numeric input is refused outright.</b> <c>Enum.TryParse</c> reads <c>"7"</c> as an
    /// ordinal and returns an undefined value, and reads <c>"0"</c> as a perfectly valid Regular.
    /// The wire contract is a mode <i>name</i>; accepting ordinals would couple clients to the
    /// declaration order of the enum, so a reordering here would silently reprice their
    /// bookings.</item>
    /// <item><b><c>Enum.IsDefined</c></b> catches anything else that slips through as an
    /// out-of-range value rather than persisting and ranking it as if it were real.</item>
    /// </list>
    /// </remarks>
    public static bool TryParse(string? value, out DeliveryMode mode)
    {
        mode = DeliveryMode.Regular;

        if (string.IsNullOrWhiteSpace(value))
            return true;

        var trimmed = value.Trim();

        // A name, never an ordinal.
        if (trimmed.All(c => char.IsAsciiDigit(c) || c is '+' or '-'))
            return false;

        return Enum.TryParse(trimmed, ignoreCase: true, out mode) && Enum.IsDefined(mode);
    }
}
