using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using BeeLogistics.Shared.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Security;

/// <summary>
/// S2S authentication for trusted service clients (back-office backend):
/// X-Client-Id + X-Api-Key validated against SHA-256 hashes with a
/// constant-time comparison, emitting backoffice-equivalent claims.
/// </summary>
public class ServiceApiKeyAuthenticationHandlerTests
{
    private const string ApiKey = "test-api-key-abc123"; // gitleaks:allow - fixture, hashed below and never a live key
    private const string ClientId = "backoffice-backend";
    private const string ServiceUserId = "11111111-1111-1111-1111-111111111111";

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static ServiceClientsOptions DefaultOptions() => new()
    {
        Clients =
        {
            new ServiceClientCredential
            {
                ClientId = ClientId,
                ApiKeyHashes = { Sha256Hex(ApiKey) },
                ServiceUserId = ServiceUserId,
            }
        }
    };

    private static async Task<AuthenticateResult> AuthenticateAsync(
        ServiceClientsOptions options,
        Action<HttpContext>? configureRequest = null)
    {
        var schemeOptions = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        schemeOptions.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        var clientOptions = Substitute.For<IOptionsMonitor<ServiceClientsOptions>>();
        clientOptions.CurrentValue.Returns(options);

        var handler = new ServiceApiKeyAuthenticationHandler(
            schemeOptions, NullLoggerFactory.Instance, UrlEncoder.Default, clientOptions);

        var context = new DefaultHttpContext();
        configureRequest?.Invoke(context);

        await handler.InitializeAsync(
            new AuthenticationScheme(ServiceApiKeyDefaults.SchemeName, null, typeof(ServiceApiKeyAuthenticationHandler)),
            context);
        return await handler.AuthenticateAsync();
    }

    [Fact]
    public async Task Valid_client_id_and_key_authenticate_with_backoffice_claims()
    {
        var result = await AuthenticateAsync(DefaultOptions(), ctx =>
        {
            ctx.Request.Headers[ServiceApiKeyDefaults.ClientIdHeader] = ClientId;
            ctx.Request.Headers[ServiceApiKeyDefaults.ApiKeyHeader] = ApiKey;
            ctx.Request.Headers[ServiceApiKeyDefaults.ActingAdminHeader] = "admin@example.com";
        });

        Assert.True(result.Succeeded);
        var principal = result.Principal!;
        Assert.Equal(ServiceUserId, principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("SuperAdmin", principal.FindFirstValue(ClaimTypes.Role));
        Assert.Equal("SuperAdmin", principal.FindFirstValue("role"));
        Assert.Equal("true", principal.FindFirstValue("is_backoffice"));
        Assert.Equal(ClientId, principal.FindFirstValue("client_id"));
        Assert.Equal("service", principal.FindFirstValue("auth_type"));
        Assert.Equal("admin@example.com", principal.FindFirstValue("acting_admin"));
    }

    [Fact]
    public async Task Wrong_key_fails()
    {
        var result = await AuthenticateAsync(DefaultOptions(), ctx =>
        {
            ctx.Request.Headers[ServiceApiKeyDefaults.ClientIdHeader] = ClientId;
            ctx.Request.Headers[ServiceApiKeyDefaults.ApiKeyHeader] = "wrong-key";
        });

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Unknown_client_id_fails()
    {
        var result = await AuthenticateAsync(DefaultOptions(), ctx =>
        {
            ctx.Request.Headers[ServiceApiKeyDefaults.ClientIdHeader] = "impostor";
            ctx.Request.Headers[ServiceApiKeyDefaults.ApiKeyHeader] = ApiKey;
        });

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Missing_client_id_fails()
    {
        var result = await AuthenticateAsync(DefaultOptions(), ctx =>
        {
            ctx.Request.Headers[ServiceApiKeyDefaults.ApiKeyHeader] = ApiKey;
        });

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task No_api_key_header_yields_no_result_so_other_schemes_can_run()
    {
        var result = await AuthenticateAsync(DefaultOptions());

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task Rotated_second_key_hash_is_accepted()
    {
        var options = DefaultOptions();
        options.Clients[0].ApiKeyHashes.Insert(0, Sha256Hex("previous-key"));

        var result = await AuthenticateAsync(options, ctx =>
        {
            ctx.Request.Headers[ServiceApiKeyDefaults.ClientIdHeader] = ClientId;
            ctx.Request.Headers[ServiceApiKeyDefaults.ApiKeyHeader] = ApiKey;
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Client_without_service_user_id_fails()
    {
        var options = DefaultOptions();
        options.Clients[0].ServiceUserId = "";

        var result = await AuthenticateAsync(options, ctx =>
        {
            ctx.Request.Headers[ServiceApiKeyDefaults.ClientIdHeader] = ClientId;
            ctx.Request.Headers[ServiceApiKeyDefaults.ApiKeyHeader] = ApiKey;
        });

        Assert.False(result.Succeeded);
    }
}
