using BeeLogistics.Modules.Identity.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Modules.Identity.Infrastructure;

/// <summary>
/// Scaffold for SMS OTP. Gated behind the "Features:SmsOtp" flag (default off).
/// Implement against an SMS provider (or Clerk phone OTP on Pro) and flip the flag —
/// no other wiring changes needed. See CLERK_MIGRATION_PLAN.md §2a.
/// </summary>
public class SmsOtpChannel : IOtpChannel
{
    private readonly bool _enabled;

    public SmsOtpChannel(IConfiguration configuration)
    {
        _enabled = configuration.GetValue<bool>("Features:SmsOtp");
    }

    public string Channel => "sms";

    public bool IsEnabled => _enabled;

    public Task SendAsync(string recipient, string code, string purpose, CancellationToken ct = default)
    {
        // TODO: implement when an SMS provider / Clerk Pro is available.
        throw new NotImplementedException("SMS OTP is not implemented yet (future / Clerk Pro).");
    }
}
