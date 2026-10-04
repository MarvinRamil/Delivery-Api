namespace BeeLogistics.Modules.Messaging.Application.Services;

/// <summary>
/// Builds the events the bot posts into a booking room.
/// </summary>
/// <remarks>
/// Everything the bot posts uses <c>msgtype: m.notice</c> rather than <c>m.text</c>. That is the
/// Matrix convention for automated messages, and clients render it differently — so a status
/// update reads as the system talking, not as the other party talking.
///
/// Each message also carries a machine-readable <c>app.bee.*</c> field alongside the human text, so
/// a client can render a status change as a chip or a timeline entry instead of parsing prose.
/// </remarks>
public static class BookingSystemMessageComposer
{
    /// <summary>Content for the notice posted when a driver is assigned.</summary>
    public static object DriverAssigned(string? driverName) => new
    {
        msgtype = "m.notice",
        body = string.IsNullOrWhiteSpace(driverName)
            ? "A driver has been assigned to this booking."
            : $"{driverName} has been assigned to this booking.",
        // Namespaced so it cannot collide with anything Matrix defines later.
        app_bee_event = "driver_assigned",
        app_bee_driver_name = driverName,
    };

    /// <summary>
    /// Content for a booking-status notice, or null for a status not worth interrupting a
    /// conversation over.
    /// </summary>
    /// <remarks>
    /// Not every status earns a line. <c>Pending</c> is the state the room is created in, and
    /// <c>Confirmed</c> is the driver assignment that <see cref="DriverAssigned"/> already
    /// announces — posting those too would say the same thing twice and train people to ignore
    /// the bot.
    /// </remarks>
    public static object? BookingStatus(string status) => status switch
    {
        "DriverAssigned" => Notice(status, "The driver is on the way to pick up."),
        "PickedUp"       => Notice(status, "The package has been picked up."),
        "InTransit"      => Notice(status, "The delivery is on its way."),
        "Completed"      => Notice(status, "This delivery is complete."),
        "Cancelled"      => Notice(status, "This booking was cancelled."),
        _ => null,
    };

    private static object Notice(string status, string body) => new
    {
        msgtype = "m.notice",
        body,
        app_bee_event = "booking_status",
        app_bee_status = status,
    };

    /// <summary>
    /// A stable transaction id for a logical send. Matrix de-duplicates on it, which is what makes
    /// a consumer retry safe: the same logical message re-sent produces the same event, not a
    /// second copy in the room.
    /// </summary>
    public static string TransactionId(Guid bookingId, string kind, string discriminator) =>
        $"bee-{bookingId:N}-{kind}-{discriminator}";
}
