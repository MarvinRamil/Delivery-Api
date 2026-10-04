using System.Net;
using System.Text;
using System.Text.Json;
using BeeLogistics.Modules.Messaging.Application;
using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Messaging;

/// <summary>
/// Pins the wire format of the Application Service API calls.
/// </summary>
/// <remarks>
/// These assertions are the substitute for a Matrix SDK. Nothing else in the codebase checks that
/// the appservice token travels as a bearer, that <c>?user_id=</c> masquerading is applied to the
/// right calls, or that "already exists" is treated as success — and every one of those, if wrong,
/// fails at runtime as "no booking gets a chat room" rather than as a compile error.
/// </remarks>
public class MatrixAppServiceClientTests
{
    private const string AsToken = "as-token-secret";

    private static MatrixOptions Options() => new()
    {
        Enabled = true,
        HomeserverUrl = "http://synapse:8008",
        ServerName = "matrix.bee-app.tech",
        EnvironmentPrefix = "dev",
        AppServiceId = "bee-appservice-dev",
        SenderLocalpart = "bee-dev",
        AsToken = AsToken,
        HsToken = "hs-token-secret",
    };

    private static (MatrixAppServiceClient Client, RecordingHandler Handler) Build(params (HttpStatusCode, string)[] responses)
    {
        var handler = new RecordingHandler(responses);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(MatrixHttpClient.Name).Returns(_ => new HttpClient(handler, false)
        {
            BaseAddress = MatrixHttpClient.NormalizeBaseAddress("http://synapse:8008"),
        });

        return (new MatrixAppServiceClient(factory, Options().ToOptions(), NullLogger<MatrixAppServiceClient>.Instance), handler);
    }

    [Fact]
    public async Task Register_uses_the_appservice_login_type_and_bearer_token()
    {
        var (client, handler) = Build((HttpStatusCode.OK, """{"user_id":"@bee_u_dev_abc:matrix.bee-app.tech"}"""));

        var mxid = await client.EnsureUserAsync("bee_u_dev_abc", displayName: null);

        Assert.Equal("@bee_u_dev_abc:matrix.bee-app.tech", mxid);
        var req = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://synapse:8008/_matrix/client/v3/register", req.Url);
        Assert.Equal($"Bearer {AsToken}", req.Authorization);

        using var body = JsonDocument.Parse(req.Body!);
        Assert.Equal("m.login.application_service", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("bee_u_dev_abc", body.RootElement.GetProperty("username").GetString());
    }

    [Fact]
    public async Task An_already_registered_user_is_success_not_failure()
    {
        // The expected outcome on every retry after the first success. If this threw, a redelivered
        // provisioning message would fail forever and the booking would never get a room.
        var (client, _) = Build((HttpStatusCode.BadRequest, """{"errcode":"M_USER_IN_USE","error":"User ID already taken."}"""));

        var mxid = await client.EnsureUserAsync("bee_u_dev_abc", displayName: null);

        Assert.Equal("@bee_u_dev_abc:matrix.bee-app.tech", mxid);
    }

    [Fact]
    public async Task A_localpart_outside_our_namespace_fails_with_a_configuration_hint()
    {
        // M_EXCLUSIVE means the registration file's regex and Matrix:EnvironmentPrefix disagree.
        // That is a config bug, and the message has to say so or it reads as a transient error.
        var (client, _) = Build((HttpStatusCode.BadRequest, """{"errcode":"M_EXCLUSIVE","error":"Invalid user localpart for this application service."}"""));

        var ex = await Assert.ThrowsAsync<MatrixApiException>(() => client.EnsureUserAsync("bee_u_prod_abc", null));

        Assert.Equal("M_EXCLUSIVE", ex.ErrCode);
        Assert.Contains("EnvironmentPrefix", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_requests_a_token_for_a_named_user_and_can_reuse_a_device()
    {
        var (client, handler) = Build((HttpStatusCode.OK,
            """{"user_id":"@bee_u_dev_abc:matrix.bee-app.tech","access_token":"syt_xxx","device_id":"DEV1"}"""));

        var session = await client.LoginAsUserAsync("bee_u_dev_abc", "DEV1", "driver app");

        Assert.Equal("syt_xxx", session.AccessToken);
        Assert.Equal("DEV1", session.DeviceId);

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("m.login.application_service", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("bee_u_dev_abc", body.RootElement.GetProperty("identifier").GetProperty("user").GetString());
        Assert.Equal("DEV1", body.RootElement.GetProperty("device_id").GetString());
    }

    [Fact]
    public async Task Login_omits_device_id_entirely_when_the_client_has_none()
    {
        // Sending device_id: null would make Synapse reject the request; the key must be absent.
        var (client, handler) = Build((HttpStatusCode.OK,
            """{"user_id":"@u:matrix.bee-app.tech","access_token":"t","device_id":"GEN"}"""));

        await client.LoginAsUserAsync("bee_u_dev_abc", deviceId: null, deviceDisplayName: null);

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.False(body.RootElement.TryGetProperty("device_id", out _));
        Assert.False(body.RootElement.TryGetProperty("initial_device_display_name", out _));
    }

    [Fact]
    public async Task CreateRoom_locks_participants_down_to_sending_messages_only()
    {
        var (client, handler) = Build((HttpStatusCode.OK, """{"room_id":"!abc:matrix.bee-app.tech"}"""));

        var result = await client.CreateOrAdoptRoomAsync(new CreateRoomRequest(
            "booking-dev-bkg-1", "BKG-1", "Pickup to dropoff",
            "@bee-dev:matrix.bee-app.tech",
            new[] { "@bee_u_dev_cust:matrix.bee-app.tech" },
            new Dictionary<string, object> { ["bookingId"] = "b1" }));

        Assert.False(result.Adopted);
        Assert.Equal("!abc:matrix.bee-app.tech", result.RoomId);

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        var pl = body.RootElement.GetProperty("power_level_content_override");
        Assert.Equal(100, pl.GetProperty("users").GetProperty("@bee-dev:matrix.bee-app.tech").GetInt32());
        Assert.Equal(0, pl.GetProperty("users_default").GetInt32());
        Assert.Equal(0, pl.GetProperty("events_default").GetInt32());   // may send messages
        Assert.Equal(100, pl.GetProperty("state_default").GetInt32());  // may not change state
        Assert.Equal(100, pl.GetProperty("invite").GetInt32());         // may not invite anyone
        Assert.Equal(100, pl.GetProperty("kick").GetInt32());
        Assert.Equal("private_chat", body.RootElement.GetProperty("preset").GetString());
    }

    [Fact]
    public async Task A_taken_alias_adopts_the_existing_room_instead_of_failing()
    {
        // The redelivery path. Creating a second room for one booking would split the conversation
        // in half, with each party in a different room.
        var (client, handler) = Build(
            (HttpStatusCode.BadRequest, """{"errcode":"M_ROOM_IN_USE","error":"Room alias already taken"}"""),
            (HttpStatusCode.OK, """{"room_id":"!existing:matrix.bee-app.tech"}"""));

        var result = await client.CreateOrAdoptRoomAsync(new CreateRoomRequest(
            "booking-dev-bkg-1", "BKG-1", null, "@bee-dev:matrix.bee-app.tech", Array.Empty<string>(), null));

        Assert.True(result.Adopted);
        Assert.Equal("!existing:matrix.bee-app.tech", result.RoomId);
        Assert.Contains("/directory/room/", handler.Requests[1].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_taken_alias_that_does_not_resolve_is_an_error_worth_reading()
    {
        // Room deleted without releasing its alias. Silently creating a differently-aliased room
        // would leave the booking permanently unable to find its own chat.
        var (client, _) = Build(
            (HttpStatusCode.BadRequest, """{"errcode":"M_ROOM_IN_USE","error":"taken"}"""),
            (HttpStatusCode.NotFound, """{"errcode":"M_NOT_FOUND"}"""));

        var ex = await Assert.ThrowsAsync<MatrixApiException>(() => client.CreateOrAdoptRoomAsync(
            new CreateRoomRequest("booking-dev-bkg-1", "BKG-1", null, "@bee-dev:m", Array.Empty<string>(), null)));

        Assert.Contains("does not resolve", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Joining_masquerades_as_the_user_but_inviting_does_not()
    {
        // The distinction that makes appservice room setup work: the bot invites, then we accept
        // on the user's behalf, because an appservice user has no client to tap "accept".
        var (client, handler) = Build((HttpStatusCode.OK, "{}"), (HttpStatusCode.OK, "{}"));

        await client.InviteAsync("!r:matrix.bee-app.tech", "@bee_u_dev_a:matrix.bee-app.tech");
        await client.JoinAsUserAsync("!r:matrix.bee-app.tech", "@bee_u_dev_a:matrix.bee-app.tech");

        Assert.DoesNotContain("user_id=", handler.Requests[0].Url, StringComparison.Ordinal);
        Assert.Contains("user_id=%40bee_u_dev_a", handler.Requests[1].Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Already_being_in_the_room_is_not_an_invite_failure()
    {
        var (client, _) = Build((HttpStatusCode.Forbidden,
            """{"errcode":"M_FORBIDDEN","error":"@x:server is already in the room."}"""));

        await client.InviteAsync("!r:matrix.bee-app.tech", "@x:server");  // must not throw
    }

    [Fact]
    public async Task Send_carries_the_transaction_id_so_a_retry_cannot_duplicate_a_message()
    {
        var (client, handler) = Build((HttpStatusCode.OK, """{"event_id":"$evt"}"""));

        var eventId = await client.SendMessageAsync("!r:matrix.bee-app.tech",
            new { msgtype = "m.notice", body = "Driver assigned" }, "txn-booking-1-status-3");

        Assert.Equal("$evt", eventId);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Contains("/send/m.room.message/txn-booking-1-status-3", handler.Requests[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Room_ids_are_escaped_so_the_sigil_cannot_break_the_path()
    {
        // Room ids start with '!' and contain ':'. Unescaped, they mangle the URL.
        var (client, handler) = Build((HttpStatusCode.OK, """{"event_id":"$e"}"""));

        await client.SendMessageAsync("!abc:matrix.bee-app.tech", new { body = "x" }, "t1");

        Assert.Contains("%21abc%3Amatrix.bee-app.tech", handler.Requests[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unresolved_alias_is_null_rather_than_an_exception()
    {
        var (client, _) = Build((HttpStatusCode.NotFound, """{"errcode":"M_NOT_FOUND"}"""));

        Assert.Null(await client.ResolveAliasAsync("#booking-dev-nope:matrix.bee-app.tech"));
    }

    private sealed record Captured(HttpMethod Method, string Url, string? Authorization, string? Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;
        public List<Captured> Requests { get; } = new();

        public RecordingHandler((HttpStatusCode, string)[] responses) => _responses = new(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new Captured(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));

            var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}

internal static class OptionsExtensions
{
    public static IOptions<T> ToOptions<T>(this T value) where T : class => Options.Create(value);
}
