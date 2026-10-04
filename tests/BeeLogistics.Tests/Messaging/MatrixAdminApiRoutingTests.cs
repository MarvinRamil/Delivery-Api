using System.Net;
using System.Text;
using BeeLogistics.Modules.Messaging.Application;
using BeeLogistics.Modules.Messaging.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Messaging;

/// <summary>
/// The admin API is the one call that does not go to <c>Matrix:HomeserverUrl</c>, and the one place
/// a 404 must not be read as success.
/// </summary>
/// <remarks>
/// <para>
/// Both halves guard the same incident. <c>/_synapse/*</c> is blocked on the public hostname while
/// <c>/_matrix/*</c> is not, so purging — the only admin call — reached a reverse proxy that
/// answered <b>404</b>. The old code treated any 404 as "the room is already gone", so the sweep
/// marked every room purged, stopped retrying them, and logged <c>"Purged N of N"</c> while every
/// room was still on the homeserver.
/// </para>
/// <para>
/// That is why the 404 tests below assert on the <em>body</em> rather than the status: a proxy's
/// 404 is HTML, Synapse's carries an <c>errcode</c>, and only the second one means the room is gone.
/// </para>
/// </remarks>
public class MatrixAdminApiRoutingTests
{
    private const string HomeserverUrl = "https://matrix.bee-app.tech";
    private const string AdminApiUrl = "http://10.10.10.152:8008";
    private const string RoomId = "!abc:matrix.bee-app.tech";

    private static MatrixOptions Options(string? adminApiUrl) => new()
    {
        Enabled = true,
        HomeserverUrl = HomeserverUrl,
        ServerName = "matrix.bee-app.tech",
        EnvironmentPrefix = "dev",
        AppServiceId = "bee-appservice-dev",
        SenderLocalpart = "bee-dev",
        AsToken = "as-token-secret",
        HsToken = "hs-token-secret",
        AdminToken = "admin-token-secret",
        AdminApiUrl = adminApiUrl ?? string.Empty,
    };

    private static (MatrixAppServiceClient Client, StubHandler Handler) Build(
        string? adminApiUrl = null,
        HttpStatusCode status = HttpStatusCode.OK,
        string body = "{}",
        string contentType = "application/json")
    {
        var handler = new StubHandler(status, body, contentType);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(MatrixHttpClient.Name).Returns(_ => new HttpClient(handler, false)
        {
            BaseAddress = MatrixHttpClient.NormalizeBaseAddress(HomeserverUrl),
        });

        return (
            new MatrixAppServiceClient(factory, Options(adminApiUrl).ToOptions(), NullLogger<MatrixAppServiceClient>.Instance),
            handler);
    }

    // --- routing ---

    [Fact]
    public async Task Purge_goes_to_the_admin_url_when_one_is_configured()
    {
        var (client, handler) = Build(adminApiUrl: AdminApiUrl);

        await client.PurgeRoomAsync(RoomId);

        Assert.Equal(
            "http://10.10.10.152:8008/_synapse/admin/v2/rooms/%21abc%3Amatrix.bee-app.tech",
            handler.LastUrl);
    }

    [Fact]
    public async Task Purge_falls_back_to_the_homeserver_url_when_none_is_configured()
    {
        // An environment that never sets AdminApiUrl must behave exactly as it did before the
        // option existed.
        var (client, handler) = Build(adminApiUrl: null);

        await client.PurgeRoomAsync(RoomId);

        Assert.StartsWith("https://matrix.bee-app.tech/_synapse/", handler.LastUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_admin_url_does_not_redirect_ordinary_appservice_calls()
    {
        // The whole point of a separate admin URL is that it moves one call and nothing else.
        // Client-server traffic keeps the public path deliberately - see docker/synapse/README.md.
        var (client, handler) = Build(
            adminApiUrl: AdminApiUrl,
            body: """{"user_id":"@bee_u_dev_abc:matrix.bee-app.tech"}""");

        await client.EnsureUserAsync("bee_u_dev_abc", displayName: null);

        Assert.StartsWith("https://matrix.bee-app.tech/_matrix/", handler.LastUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Purge_sends_the_admin_token_not_the_appservice_token()
    {
        var (client, handler) = Build(adminApiUrl: AdminApiUrl);

        await client.PurgeRoomAsync(RoomId);

        Assert.Equal("Bearer admin-token-secret", handler.LastAuthorization);
    }

    // --- what a 404 means ---

    [Fact]
    public async Task A_404_from_Synapse_means_the_room_is_already_gone()
    {
        var (client, _) = Build(
            adminApiUrl: AdminApiUrl,
            status: HttpStatusCode.NotFound,
            body: """{"errcode":"M_NOT_FOUND","error":"Unknown room"}""");

        // Purging something already purged is the outcome we wanted, so this must not throw.
        await client.PurgeRoomAsync(RoomId);
    }

    [Fact]
    public async Task A_404_from_a_proxy_is_a_failure_not_a_successful_purge()
    {
        // The exact response that caused the incident: NPM refusing /_synapse/*.
        var (client, _) = Build(
            adminApiUrl: AdminApiUrl,
            status: HttpStatusCode.NotFound,
            body: "<html>\n<head><title>404 Not Found</title></head>\n<body>\n<center><h1>404 Not Found</h1></center>\n<hr><center>openresty</center>\n</body>\n</html>",
            contentType: "text/html");

        var ex = await Assert.ThrowsAsync<MatrixApiException>(() => client.PurgeRoomAsync(RoomId));

        // The message has to name the host, because "404" alone is what made this invisible.
        Assert.Contains("10.10.10.152:8008", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Matrix:AdminApiUrl", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_404_body_is_a_failure_too()
    {
        // Not every proxy bothers with a body.
        var (client, _) = Build(
            adminApiUrl: AdminApiUrl,
            status: HttpStatusCode.NotFound,
            body: string.Empty,
            contentType: "text/plain");

        await Assert.ThrowsAsync<MatrixApiException>(() => client.PurgeRoomAsync(RoomId));
    }

    [Fact]
    public async Task Purging_without_an_admin_token_fails_before_any_request_is_made()
    {
        var options = Options(AdminApiUrl);
        options.AdminToken = string.Empty;

        var handler = new StubHandler(HttpStatusCode.OK, "{}", "application/json");
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(MatrixHttpClient.Name).Returns(_ => new HttpClient(handler, false)
        {
            BaseAddress = MatrixHttpClient.NormalizeBaseAddress(HomeserverUrl),
        });
        var client = new MatrixAppServiceClient(factory, options.ToOptions(), NullLogger<MatrixAppServiceClient>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PurgeRoomAsync(RoomId));
        Assert.Null(handler.LastUrl);
    }

    // --- configuration ---

    [Fact]
    public void AdminApiBaseUrl_falls_back_to_the_homeserver_url()
    {
        Assert.Equal(HomeserverUrl, Options(adminApiUrl: null).AdminApiBaseUrl);
        Assert.Equal(AdminApiUrl, Options(AdminApiUrl).AdminApiBaseUrl);
    }

    [Theory]
    [InlineData("10.10.10.152:8008")]   // reads as scheme "10.10.10.152" - the trap IsUsableHomeserverUrl exists for
    [InlineData("matrix.bee-app.tech")]
    [InlineData("not a url")]
    public void An_unusable_admin_url_is_rejected_at_startup(string configured)
    {
        var options = Options(configured);

        Assert.Contains(options.Validate(), p => p.Contains("AdminApiUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unset_admin_url_is_not_a_configuration_problem()
    {
        Assert.DoesNotContain(
            Options(adminApiUrl: null).Validate(),
            p => p.Contains("AdminApiUrl", StringComparison.Ordinal));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly string _contentType;

        public string? LastUrl { get; private set; }
        public string? LastAuthorization { get; private set; }

        public StubHandler(HttpStatusCode status, string body, string contentType)
        {
            _status = status;
            _body = body;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri!.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, _contentType),
            });
        }
    }
}
