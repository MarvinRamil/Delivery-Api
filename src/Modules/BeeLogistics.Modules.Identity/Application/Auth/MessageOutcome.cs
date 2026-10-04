namespace BeeLogistics.Modules.Identity.Application.Auth;

/// <summary>
/// Which HTTP shape an auth endpoint should render for an outcome.
/// </summary>
/// <remarks>
/// Only the statuses the auth endpoints actually produce. <c>Unauthorized</c> is deliberately
/// absent: the endpoints that can return 401 do so from a claims check the controller performs
/// before dispatching, which is a presentation concern, not a handler one.
/// </remarks>
public enum AuthOutcomeStatus
{
    Ok,
    BadRequest,
    NotFound
}

/// <summary>
/// The outcome of an auth operation whose endpoint renders a plain
/// <c>{ success, message }</c> body - which is most of them.
/// </summary>
/// <remarks>
/// Deliberately NOT <c>Result&lt;T&gt;</c>. Auth endpoints do not use
/// <c>BaseController.FromResult</c> and therefore do not wrap responses in the
/// <c>ApiResponse</c> envelope; clients read <c>message</c> at the top level of the body rather
/// than under <c>data</c>. Handlers return this and each action renders it into the same
/// anonymous object it returned before, so the wire format cannot drift. See issue #45.
/// <para>
/// Slices whose responses carry extra fields declare their own outcome type instead - see
/// <c>Otp.OtpOutcome</c> (registration token) and <c>Password.SecurityQuestionsOutcome</c>.
/// </para>
/// </remarks>
public sealed record MessageOutcome(AuthOutcomeStatus Status, string Message)
{
    public bool Success => Status == AuthOutcomeStatus.Ok;

    public static MessageOutcome Ok(string message) => new(AuthOutcomeStatus.Ok, message);
    public static MessageOutcome Fail(string message) => new(AuthOutcomeStatus.BadRequest, message);

    /// <summary>Renders as <c>NotFound(message)</c> - a bare string body, not the JSON envelope.</summary>
    public static MessageOutcome NotFound(string message) => new(AuthOutcomeStatus.NotFound, message);
}
