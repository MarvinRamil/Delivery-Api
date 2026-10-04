namespace BeeLogistics.Modules.Identity.Application.Auth;

/// <summary>
/// Every user-facing message the auth endpoints return, in one place.
/// </summary>
/// <remarks>
/// These strings are part of the API contract: clients match on them. <c>bee-driver</c>'s
/// <c>authService.ts</c> branches on the exact text of <see cref="InvalidCredentials"/>,
/// <see cref="AccountDeactivated"/> and the word "locked" to choose which error to show the
/// driver. Changing the wording here changes app behaviour, so treat edits as contract changes.
/// <para>
/// They were previously spread across <c>AuthController</c>'s <c>MSG_*</c> constants, a
/// per-slice <c>OtpMessages</c> holder, and bare literals inside individual handlers - the same
/// sentence declared in up to four places. Consolidated so a reword cannot leave endpoints
/// disagreeing.
/// </para>
/// <para>
/// Near-identical strings are deliberately kept separate where the endpoints genuinely differ:
/// see <see cref="NewPasswordTooShort"/> vs <see cref="PasswordTooShort"/>.
/// </para>
/// </remarks>
public static class AuthMessages
{
    // --- credentials and account state -------------------------------------

    /// <summary>
    /// SECURITY: returned for unknown user, wrong password, and every other sign-in failure
    /// alike. The uniformity is the point - it prevents account enumeration - so do not split
    /// this into more specific messages.
    /// </summary>
    public const string InvalidCredentials = "Invalid credentials";

    public const string AccountLocked = "Account is temporarily locked. Please try again later.";
    public const string AccountDeactivated = "Account is deactivated";
    public const string EmailNotVerified = "Please verify your email address before logging in. Check your inbox for the verification link.";
    public const string UserNotFound = "User not found";

    // --- request validation -------------------------------------------------

    public const string EmailRequired = "Email is required";
    public const string EmailTooLong = "Email is too long";
    public const string InvalidEmailFormat = "Invalid email format";
    public const string PhoneRequired = "Phone number is required";
    public const string InvalidPhoneFormat = "Invalid phone number format";
    public const string PasswordRequired = "Password is required";
    public const string PasswordTooLong = "Password is too long";

    /// <summary>Used by the password-reset flow.</summary>
    public const string PasswordTooShort = "Password must be at least 8 characters";

    /// <summary>
    /// Used by the change-password flow. Deliberately worded differently from
    /// <see cref="PasswordTooShort"/> - both are live and clients may match on either.
    /// </summary>
    public const string NewPasswordTooShort = "New password must be at least 8 characters";

    public const string PasswordComplexityFailed = "Password does not meet complexity requirements";

    // --- tokens -------------------------------------------------------------

    public const string RefreshTokenRequired = "Refresh token is required";
    public const string InvalidOrExpiredToken = "Invalid or expired token";

    // --- otp ----------------------------------------------------------------

    /// <summary>
    /// SECURITY: the lockout message is identical across email and phone OTP so the endpoint
    /// cannot be used to tell which identifiers exist.
    /// </summary>
    public const string TooManyAttempts = "Too many failed attempts. Please try again later.";
}
