namespace BeeLogistics.Modules.Payment.Application.Options;

/// <summary>
/// Gateway selection ("Payments" section). ActiveGateway routes NEW payments,
/// top-ups, withdrawals, and newly saved payment methods only — existing records
/// always resolve their gateway from the Provider stored on the record, so
/// flipping this switch never strands in-flight payments or refunds.
/// </summary>
public sealed class PaymentGatewayOptions
{
    public const string SectionName = "Payments";

    /// <summary>"paymongo" or "xendit" (case-insensitive).</summary>
    public string ActiveGateway { get; set; } = "paymongo";
}
