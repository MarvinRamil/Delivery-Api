using System.Text.Json;
using BeeLogistics.Modules.Messaging.Application;
using BeeLogistics.Modules.Messaging.Application.Services;
using BeeLogistics.Modules.Messaging.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Messaging.Presentation.Controllers;

/// <summary>
/// The homeserver's side of the appservice contract: Synapse pushes every event in our namespace
/// here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately outside <c>/api</c> and outside the app's normal authentication.</b> Synapse is
/// not a bee user and holds no bee JWT; it authenticates with the <c>hs_token</c> from the
/// registration file. Hence <c>[AllowAnonymous]</c> plus an explicit constant-time token check —
/// not an absence of auth, a different one.
/// </para>
/// <para>
/// The path is <c>/appservice</c> + Matrix's fixed <c>/_matrix/app/v1/…</c> suffix, matching the
/// <c>url</c> in the registration file.
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("appservice/_matrix/app/v1")]
public class MatrixAppServiceController : ControllerBase
{
    private readonly IMatrixTransactionProcessor _processor;
    private readonly MatrixOptions _options;
    private readonly ILogger<MatrixAppServiceController> _logger;

    public MatrixAppServiceController(
        IMatrixTransactionProcessor processor,
        IOptions<MatrixOptions> options,
        ILogger<MatrixAppServiceController> logger)
    {
        _processor = processor;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Receives a batch of room events from Synapse.
    /// </summary>
    /// <remarks>
    /// Answers <c>200 {}</c> for anything it managed to take responsibility for, because Synapse
    /// retries a transaction until it gets a 2xx and blocks that appservice's queue while it does.
    /// Idempotency lives in the unique index on the Matrix event id, so a redelivery is a no-op
    /// rather than a duplicate — which is what makes answering 200 safe rather than lossy.
    /// </remarks>
    [HttpPut("transactions/{txnId}")]
    public async Task<IActionResult> Transaction(string txnId, CancellationToken ct)
    {
        if (!Authenticated())
            return Unauthorized(new { errcode = "M_FORBIDDEN", error = "Bad hs_token" });

        if (!_options.Enabled)
        {
            // Acknowledge rather than 503: a rejected transaction is retried forever and stalls
            // the homeserver's queue for this appservice.
            _logger.LogWarning("Matrix disabled but a transaction arrived; acknowledging and discarding");
            return Ok(new { });
        }

        JsonElement body;
        try
        {
            using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
            body = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            // Malformed JSON will never parse on retry, so 400 rather than making Synapse loop.
            _logger.LogError(ex, "Malformed appservice transaction {TxnId}", txnId);
            return BadRequest(new { errcode = "M_NOT_JSON", error = "Body is not valid JSON" });
        }

        await _processor.ProcessAsync(body, ct);
        return Ok(new { });
    }

    /// <summary>
    /// Synapse probes this for a user in our namespace it has not seen.
    /// </summary>
    /// <remarks>
    /// We create users eagerly during provisioning and never lazily, so the honest answer is always
    /// "not found". Implemented rather than left to 404 as an unmatched route so the response is a
    /// proper Matrix error and the logs stay quiet.
    /// </remarks>
    [HttpGet("users/{userId}")]
    public IActionResult QueryUser(string userId) =>
        Authenticated()
            ? NotFound(new { errcode = "M_NOT_FOUND", error = "Users are provisioned eagerly; none to create on demand" })
            : Unauthorized(new { errcode = "M_FORBIDDEN", error = "Bad hs_token" });

    /// <summary>Same as <see cref="QueryUser"/>, for room aliases. Rooms are created eagerly too.</summary>
    [HttpGet("rooms/{roomAlias}")]
    public IActionResult QueryRoom(string roomAlias) =>
        Authenticated()
            ? NotFound(new { errcode = "M_NOT_FOUND", error = "Rooms are provisioned eagerly; none to create on demand" })
            : Unauthorized(new { errcode = "M_FORBIDDEN", error = "Bad hs_token" });

    /// <summary>
    /// The hs_token is the only thing separating this endpoint from anyone who finds it: a forged
    /// transaction could write arbitrary messages into the admin transcript, attributed to anyone.
    /// </summary>
    private bool Authenticated()
    {
        var presented = MatrixTransactionAuthenticator.ExtractBearer(Request.Headers.Authorization);
        var ok = MatrixTransactionAuthenticator.IsAuthentic(presented, _options.HsToken);

        if (!ok)
            _logger.LogWarning("Rejected an appservice request with a bad or missing hs_token from {Ip}",
                HttpContext.Connection.RemoteIpAddress);

        return ok;
    }
}
