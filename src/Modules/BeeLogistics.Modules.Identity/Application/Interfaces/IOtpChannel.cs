namespace BeeLogistics.Modules.Identity.Application.Interfaces;

/// <summary>
/// Abstraction over OTP delivery channels. Email OTP is owned by Clerk, so the only
/// backend channel is SMS — currently a scaffold for a future (Pro) implementation.
/// </summary>
public interface IOtpChannel
{
    /// <summary>Channel key, e.g. "sms".</summary>
    string Channel { get; }

    /// <summary>True when the channel is configured and implemented.</summary>
    bool IsEnabled { get; }

    /// <summary>Send an OTP code to the recipient for the given purpose.</summary>
    Task SendAsync(string recipient, string code, string purpose, CancellationToken ct = default);
}
