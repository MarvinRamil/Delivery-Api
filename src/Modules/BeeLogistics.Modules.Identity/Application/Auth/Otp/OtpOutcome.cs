namespace BeeLogistics.Modules.Identity.Application.Auth.Otp;

/// <summary>
/// What an OTP operation decided, in the terms the endpoint needs to render it.
/// </summary>
/// <remarks>
/// Deliberately NOT <c>Result&lt;T&gt;</c>. The auth endpoints do not use
/// <c>BaseController.FromResult</c> and therefore do not wrap responses in the
/// <c>ApiResponse</c> envelope - clients read <c>message</c> at the top level of the body, not
/// under <c>data</c>. Handlers return this, and each action renders it into the exact same
/// anonymous object it returned before, so the wire format cannot drift. See issue #45.
/// <para>
/// <see cref="RegistrationToken"/> is only meaningful for the verify operations; the send/resend
/// actions never read it, and must not serialize it.
/// </para>
/// </remarks>
public sealed record OtpOutcome(bool Success, string Message, string? RegistrationToken = null)
{
    public static OtpOutcome Fail(string message) => new(false, message);
    public static OtpOutcome Ok(string message, string? registrationToken = null) => new(true, message, registrationToken);
}

