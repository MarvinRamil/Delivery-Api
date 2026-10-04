namespace BeeLogistics.Modules.Notification.Application.Interfaces;

public interface ISmsService
{
    /// <summary>
    /// Send an SMS message to a single phone number.
    /// Implementations should handle provider-specific concerns (rate limits, logging, retries).
    /// </summary>
    /// <param name="toPhoneNumber">Destination phone number in normalized format (e.g. 639171234567).</param>
    /// <param name="message">Message body to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the provider accepted the message for delivery; false otherwise.</returns>
    Task<bool> SendAsync(string toPhoneNumber, string message, CancellationToken ct = default);
}

