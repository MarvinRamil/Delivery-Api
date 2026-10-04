namespace BeeLogistics.Modules.Drivers.Application;

/// <summary>
/// Resolves a user-supplied vehicle type against the catalog of active vehicle types
/// (owned by the Bookings module and read through
/// <see cref="BeeLogistics.Shared.Contracts.GetActiveVehicleTypesQuery"/>).
///
/// The vehicle pricing table is the single source of truth for which vehicle types exist:
/// a driver must never be onboarded into a class no booking can be priced for, because
/// they could never be fare-matched.
/// </summary>
public static class VehicleTypeResolver
{
    /// <summary>
    /// Returns the canonical spelling of <paramref name="submitted"/>, or null when it
    /// matches no active vehicle type.
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively so a client sending "sedan" still resolves, but the
    /// spelling returned is always the catalog's — the stored value has to match the
    /// pricing row for fare lookup to succeed later. The active set is ~11 entries.
    /// </remarks>
    public static string? ResolveCanonical(IReadOnlyList<string> activeVehicleTypes, string? submitted)
    {
        if (string.IsNullOrWhiteSpace(submitted)) return null;

        var trimmed = submitted.Trim();
        return activeVehicleTypes
            .FirstOrDefault(type => type.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
