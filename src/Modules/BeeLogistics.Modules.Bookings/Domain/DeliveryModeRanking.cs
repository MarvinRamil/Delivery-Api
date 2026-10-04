namespace BeeLogistics.Modules.Bookings.Domain;

/// <summary>
/// Maps a <see cref="DeliveryMode"/> to the dispatch rank persisted on
/// <see cref="Booking.DispatchPriority"/>.
/// </summary>
/// <remarks>
/// Lives in Domain rather than beside the wire-parsing in
/// <c>Application.Services.DeliveryModePolicy</c> because <see cref="Booking"/>'s constructor needs
/// it, and Domain must not reference Application (see docs/MODULE_LAYERING.md). Parsing a request
/// string is an application concern; knowing what a mode outranks is not.
/// </remarks>
public static class DeliveryModeRanking
{
    /// <summary>Served before everything else.</summary>
    public const int OnDemand = 100;

    /// <summary>The baseline. Existing rows are backfilled to this by the migration default.</summary>
    public const int Regular = 50;

    /// <summary>Deliberately patient: a pooled booking yields to anything else waiting.</summary>
    public const int Pooling = 10;

    /// <summary>
    /// The rank for a mode. Gaps between the values are intentional, so a future tier can be
    /// slotted in without a data migration.
    /// </summary>
    public static int DispatchPriorityOf(DeliveryMode mode) => mode switch
    {
        DeliveryMode.OnDemand => OnDemand,
        DeliveryMode.Pooling => Pooling,
        _ => Regular,
    };
}
