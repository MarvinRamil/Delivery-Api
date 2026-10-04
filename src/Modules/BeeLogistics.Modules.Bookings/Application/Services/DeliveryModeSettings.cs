using BeeLogistics.Modules.Bookings.Domain;
using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// Resolves a dispatch knob for one delivery mode: the per-mode override if present, otherwise the
/// flat <c>BookingSettings</c> key the code already used, otherwise the caller's default.
/// </summary>
/// <remarks>
/// The middle step is the whole point of this class. <see cref="DeliveryMode.Regular"/> ships with
/// no overrides, so every Regular value resolves to <b>exactly the flat key it resolved to before
/// this existed</b> — which is what makes "modes change dispatch" a safe change rather than a
/// rewrite of how every booking is dispatched. It is also why the existing dispatch tests, which
/// set flat keys, needed no edits.
///
/// Mode names come from the enum, so a section is <c>BookingSettings:DeliveryModes:OnDemand:…</c>.
/// </remarks>
public static class DeliveryModeSettings
{
    private const string Section = "BookingSettings";

    /// <summary>
    /// <c>BookingSettings:DeliveryModes:{mode}:{key}</c>, then <c>BookingSettings:{key}</c>, then
    /// <paramref name="fallback"/>.
    /// </summary>
    public static T Value<T>(IConfiguration configuration, DeliveryMode mode, string key, T fallback)
    {
        if (TryRead(configuration, $"{Section}:DeliveryModes:{mode}:{key}", out T modeValue))
            return modeValue;

        if (TryRead(configuration, $"{Section}:{key}", out T flatValue))
            return flatValue;

        return fallback;
    }

    /// <summary>
    /// Reads one key, treating missing, blank and unconvertible values alike as "not configured".
    /// </summary>
    /// <remarks>
    /// The try/catch is deliberate, and is the same lesson as <c>DeliveryModePricing</c>:
    /// <c>GetValue&lt;T&gt;</c> throws on a value it cannot convert, and every caller of this sits
    /// on the dispatch path — inside a Hangfire tick that pulses every due booking, or inside the
    /// broadcast consumer. One malformed entry in <c>BookingSettings</c> would otherwise stop
    /// dispatch for <i>all</i> bookings rather than falling back to the default for one knob.
    /// </remarks>
    private static bool TryRead<T>(IConfiguration configuration, string path, out T value)
    {
        value = default!;

        var raw = configuration[path];
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        try
        {
            value = configuration.GetValue<T>(path)!;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
