using BeeLogistics.Modules.Identity.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BeeLogistics.Modules.Identity.Presentation.Controllers;

/// <summary>
/// Scaffold for driver phone (SMS-OTP) sign-in. Until SMS is implemented (future / Clerk Pro),
/// these endpoints return 501 so the mobile apps can wire the flow behind a feature flag.
///
/// Intended flow once enabled (see CLERK_MIGRATION_PLAN.md §2a):
///   start  → send SMS OTP via IOtpChannel ("sms")
///   verify → validate code, then create a Clerk sign-in token (Backend API
///            createSignInToken) and return it; the app exchanges it for a Clerk session
///            via signIn.create({ strategy: 'ticket', ticket }).
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/auth/phone")]
public class PhoneAuthController : ControllerBase
{
    private readonly IOtpChannel _smsChannel;

    public PhoneAuthController(IOtpChannel smsChannel)
    {
        _smsChannel = smsChannel;
    }

    public record PhoneStartRequest(string PhoneNumber);
    public record PhoneVerifyRequest(string PhoneNumber, string Code);

    [HttpPost("start")]
    public IActionResult Start([FromBody] PhoneStartRequest request)
    {
        if (!_smsChannel.IsEnabled)
        {
            return StatusCode(StatusCodes.Status501NotImplemented,
                new { error = "Phone sign-in is not available yet." });
        }
        // TODO: generate + store OTP, send via _smsChannel, return a challenge id.
        return StatusCode(StatusCodes.Status501NotImplemented,
            new { error = "Phone sign-in is not available yet." });
    }

    [HttpPost("verify")]
    public IActionResult Verify([FromBody] PhoneVerifyRequest request)
    {
        if (!_smsChannel.IsEnabled)
        {
            return StatusCode(StatusCodes.Status501NotImplemented,
                new { error = "Phone sign-in is not available yet." });
        }
        // TODO: validate OTP, then create a Clerk sign-in token and return it.
        return StatusCode(StatusCodes.Status501NotImplemented,
            new { error = "Phone sign-in is not available yet." });
    }
}
