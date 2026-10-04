using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using BeeLogistics.Modules.Messaging.Application;
using BeeLogistics.Modules.Messaging.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Messaging.Infrastructure;

/// <summary>
/// Talks to Synapse over the Client-Server API using the appservice token.
/// </summary>
/// <remarks>
/// Modelled on <c>DiditApiClient</c>: a typed HttpClient, raw JSON handling, no SDK. Two things
/// make it an <em>appservice</em> client rather than a plain one — the <c>as_token</c> bearer, and
/// <c>?user_id=</c> masquerading to act as a namespaced user.
///
/// Every method here is written to be safe to retry, because every caller is a MassTransit
/// consumer that will be retried. "Already exists" is consistently treated as success, not as an
/// error to propagate.
/// </remarks>
public sealed class MatrixAppServiceClient : IMatrixAppServiceClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly MatrixOptions _options;
    private readonly ILogger<MatrixAppServiceClient> _logger;

    public MatrixAppServiceClient(
        IHttpClientFactory httpClientFactory,
        IOptions<MatrixOptions> options,
        ILogger<MatrixAppServiceClient> logger)
    {
        _http = httpClientFactory.CreateClient(MatrixHttpClient.Name);
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> EnsureUserAsync(string localpart, string? displayName, CancellationToken ct = default)
    {
        var mxid = $"@{localpart}:{_options.ServerName}";

        using var request = Authorized(HttpMethod.Post, "_matrix/client/v3/register");
        request.Content = JsonContent.Create(new
        {
            type = "m.login.application_service",
            username = localpart,
        }, options: Json);

        using var response = await _http.SendAsync(request, ct);

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Provisioned Matrix user {MatrixUserId}", mxid);
            if (!string.IsNullOrWhiteSpace(displayName))
                await TrySetDisplayNameAsync(mxid, displayName, ct);
            return mxid;
        }

        var errcode = await ReadErrCodeAsync(response, ct);

        // The expected outcome on every retry after the first success.
        if (errcode == "M_USER_IN_USE")
        {
            _logger.LogDebug("Matrix user {MatrixUserId} already exists", mxid);
            return mxid;
        }

        // Worth its own message: this means the localpart does not match the namespace regex in
        // the registration file, which is a configuration bug, not a transient failure.
        if (errcode == "M_EXCLUSIVE")
        {
            throw new MatrixApiException(
                $"Synapse refused '{localpart}' as outside this appservice's exclusive namespace. " +
                $"Check that Matrix:EnvironmentPrefix ('{_options.EnvironmentPrefix}') matches the " +
                "users regex in the registration file.", response.StatusCode, errcode);
        }

        throw await FailureAsync(response, $"register {mxid}", ct);
    }

    public async Task<MatrixSession> LoginAsUserAsync(
        string localpart, string? deviceId, string? deviceDisplayName, CancellationToken ct = default)
    {
        using var request = Authorized(HttpMethod.Post, "_matrix/client/v3/login");
        request.Content = JsonContent.Create(new Dictionary<string, object?>
        {
            ["type"] = "m.login.application_service",
            ["identifier"] = new { type = "m.id.user", user = localpart },
            ["device_id"] = deviceId,
            ["initial_device_display_name"] = deviceDisplayName,
        }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value), options: Json);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw await FailureAsync(response, $"login as {localpart}", ct);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        return new MatrixSession(
            root.GetProperty("user_id").GetString()!,
            root.GetProperty("access_token").GetString()!,
            root.GetProperty("device_id").GetString()!);
    }

    public async Task<RoomCreationResult> CreateOrAdoptRoomAsync(CreateRoomRequest req, CancellationToken ct = default)
    {
        var initialState = new List<object>
        {
            new { type = "m.room.guest_access", state_key = "", content = new { guest_access = "forbidden" } },
            new { type = "m.room.history_visibility", state_key = "", content = new { history_visibility = "invited" } },
        };

        if (req.BookingState is not null)
            initialState.Add(new { type = "app.bee.booking", state_key = "", content = req.BookingState });

        using var request = Authorized(HttpMethod.Post, "_matrix/client/v3/createRoom");
        request.Content = JsonContent.Create(new
        {
            room_alias_name = req.AliasLocalpart,
            name = req.Name,
            topic = req.Topic,
            preset = "private_chat",
            visibility = "private",
            is_direct = false,
            invite = req.InviteUserIds,
            initial_state = initialState,
            // Participants may send messages and do nothing else: no invites, no kicks, no state
            // changes, no redactions of anyone else's messages. The bot owns the room.
            power_level_content_override = new
            {
                users = new Dictionary<string, int> { [req.BotUserId] = 100 },
                users_default = 0,
                events_default = 0,
                state_default = 100,
                invite = 100,
                kick = 100,
                ban = 100,
                redact = 100,
            },
        }, options: Json);

        using var response = await _http.SendAsync(request, ct);

        if (response.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return new RoomCreationResult(doc.RootElement.GetProperty("room_id").GetString()!, Adopted: false);
        }

        if (await ReadErrCodeAsync(response, ct) == "M_ROOM_IN_USE")
        {
            // Expected on redelivery. The alias carries the environment prefix, so the room it
            // names can only be this environment's room for this booking.
            var alias = $"#{req.AliasLocalpart}:{_options.ServerName}";
            var existing = await ResolveAliasAsync(alias, ct)
                ?? throw new MatrixApiException(
                    $"Alias {alias} is in use but does not resolve. The room was likely deleted " +
                    "without releasing its alias; release it on Synapse before retrying.",
                    HttpStatusCode.Conflict, "M_ROOM_IN_USE");

            _logger.LogInformation("Adopted existing room {RoomId} for alias {Alias}", existing, alias);
            return new RoomCreationResult(existing, Adopted: true);
        }

        throw await FailureAsync(response, $"createRoom {req.AliasLocalpart}", ct);
    }

    public async Task InviteAsync(string roomId, string matrixUserId, CancellationToken ct = default)
    {
        using var request = Authorized(HttpMethod.Post, $"_matrix/client/v3/rooms/{Escape(roomId)}/invite");
        request.Content = JsonContent.Create(new { user_id = matrixUserId }, options: Json);

        using var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;

        // Both mean the end state we wanted is already true.
        var errcode = await ReadErrCodeAsync(response, ct);
        if (errcode == "M_FORBIDDEN" && await IsAlreadyInRoomAsync(response, ct)) return;
        if (errcode == "M_LIMIT_EXCEEDED") throw await FailureAsync(response, "invite", ct);

        throw await FailureAsync(response, $"invite {matrixUserId} to {roomId}", ct);
    }

    public async Task JoinAsUserAsync(string roomId, string matrixUserId, CancellationToken ct = default)
    {
        using var request = Authorized(HttpMethod.Post, $"_matrix/client/v3/rooms/{Escape(roomId)}/join", asUserId: matrixUserId);
        request.Content = JsonContent.Create(new { }, options: Json);

        using var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;

        throw await FailureAsync(response, $"join {roomId} as {matrixUserId}", ct);
    }

    public async Task<string> SendMessageAsync(
        string roomId, object content, string transactionId, string? asUserId = null, CancellationToken ct = default)
    {
        using var request = Authorized(
            HttpMethod.Put,
            $"_matrix/client/v3/rooms/{Escape(roomId)}/send/m.room.message/{Escape(transactionId)}",
            asUserId);
        request.Content = JsonContent.Create(content, options: Json);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw await FailureAsync(response, $"send to {roomId}", ct);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("event_id").GetString()!;
    }

    public async Task<string?> ResolveAliasAsync(string alias, CancellationToken ct = default)
    {
        using var request = Authorized(HttpMethod.Get, $"_matrix/client/v3/directory/room/{Escape(alias)}");
        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw await FailureAsync(response, $"resolve {alias}", ct);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("room_id").GetString();
    }

    public async Task SetPowerLevelsAsync(string roomId, object content, CancellationToken ct = default)
    {
        using var request = Authorized(
            HttpMethod.Put, $"_matrix/client/v3/rooms/{Escape(roomId)}/state/m.room.power_levels/");
        request.Content = JsonContent.Create(content, options: Json);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw await FailureAsync(response, $"set power levels on {roomId}", ct);
    }

    public async Task PurgeRoomAsync(string roomId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.AdminToken))
            throw new InvalidOperationException(
                "Matrix:AdminToken is not configured, so rooms cannot be purged. See docker/synapse/README.md.");

        // The admin API is the one call that does not go to HomeserverUrl. See
        // MatrixOptions.AdminApiUrl: /_synapse/* is blocked on the public hostname, /_matrix/* is not.
        var adminBase = MatrixHttpClient.NormalizeBaseAddress(_options.AdminApiBaseUrl);
        var target = new Uri(adminBase, $"_synapse/admin/v2/rooms/{Escape(roomId)}");

        using var request = new HttpRequestMessage(HttpMethod.Delete, target)
        {
            // block:false - the room is finished, not abusive; blocking would stop it being
            // recreated under the same id, which we never do anyway.
            Content = JsonContent.Create(new { purge = true, block = false }, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AdminToken);

        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // "Already gone" is the outcome we wanted - but only Synapse gets to say so.
            //
            // A reverse proxy that refuses /_synapse/* answers 404 too, and treating that as
            // success is not a harmless mistake: the caller marks the room purged, the row stops
            // coming back for retry, and the sweep logs "Purged N of N" while every room is still
            // on the homeserver. Requiring a Matrix error body is what separates the two, because
            // a proxy's 404 is HTML and Synapse's carries an errcode.
            var errcode = await ReadErrCodeAsync(response, ct);
            if (errcode is not null) return;

            throw await FailureAsync(
                response,
                $"purge {roomId}: got 404 with no Matrix error body from {target.GetLeftPart(UriPartial.Authority)}, " +
                "so the admin API is not reachable there rather than the room being gone. " +
                "Check Matrix:AdminApiUrl - see docker/synapse/README.md",
                ct);
        }

        if (!response.IsSuccessStatusCode)
            throw await FailureAsync(response, $"purge {roomId}", ct);
    }

    private async Task TrySetDisplayNameAsync(string mxid, string displayName, CancellationToken ct)
    {
        try
        {
            using var request = Authorized(
                HttpMethod.Put, $"_matrix/client/v3/profile/{Escape(mxid)}/displayname", asUserId: mxid);
            request.Content = JsonContent.Create(new { displayname = displayName }, options: Json);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Could not set display name for {MatrixUserId}: {Status}", mxid, response.StatusCode);
        }
        catch (Exception ex)
        {
            // Cosmetic. A missing display name must never fail provisioning and strand a booking
            // without a chat room.
            _logger.LogWarning(ex, "Could not set display name for {MatrixUserId}", mxid);
        }
    }

    /// <summary>
    /// Builds a request carrying the as_token, and optionally masquerading as one of our users.
    /// </summary>
    /// <remarks>
    /// Relative paths, never leading-slash — the base address may carry a path prefix. See
    /// <see cref="MatrixHttpClient"/>.
    /// </remarks>
    private HttpRequestMessage Authorized(HttpMethod method, string path, string? asUserId = null)
    {
        var uri = asUserId is null ? path : $"{path}?user_id={HttpUtility.UrlEncode(asUserId)}";
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AsToken);
        return request;
    }

    private static string Escape(string segment) => Uri.EscapeDataString(segment);

    private static async Task<string?> ReadErrCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("errcode", out var e) ? e.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> IsAlreadyInRoomAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Synapse reports "already in the room" as a generic M_FORBIDDEN; the prose is the only
        // discriminator it offers.
        var body = await response.Content.ReadAsStringAsync(ct);
        return body.Contains("already in the room", StringComparison.OrdinalIgnoreCase)
            || body.Contains("is already joined", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<MatrixApiException> FailureAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var errcode = await ReadErrCodeAsync(response, ct);
        _logger.LogError("Matrix {What} failed: {Status} {Body}", what, (int)response.StatusCode, Truncate(body));
        return new MatrixApiException($"Matrix {what} failed: {(int)response.StatusCode} {Truncate(body)}",
            response.StatusCode, errcode);
    }

    private static string Truncate(string s) => s.Length <= 400 ? s : s[..400] + "…";
}

public sealed class MatrixApiException : Exception
{
    public MatrixApiException(string message, HttpStatusCode statusCode, string? errCode) : base(message)
    {
        StatusCode = statusCode;
        ErrCode = errCode;
    }

    public HttpStatusCode StatusCode { get; }
    public string? ErrCode { get; }
}
