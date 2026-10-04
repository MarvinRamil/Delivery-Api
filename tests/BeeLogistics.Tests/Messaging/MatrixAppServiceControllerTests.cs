using System.Text;
using System.Text.Json;
using BeeLogistics.Modules.Messaging.Application;
using BeeLogistics.Modules.Messaging.Application.Services;
using BeeLogistics.Modules.Messaging.Presentation.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Messaging;

/// <summary>
/// The endpoint Synapse calls. Two behaviours dominate: reject anything without the right
/// hs_token, and answer 200 for everything else — because Synapse retries a non-2xx forever and
/// stalls its queue for this appservice while it does.
/// </summary>
public class MatrixAppServiceControllerTests
{
    private const string HsToken = "hs-token-value";

    private readonly IMatrixTransactionProcessor _processor = Substitute.For<IMatrixTransactionProcessor>();

    private MatrixAppServiceController Build(string? authorization, string body = """{"events":[]}""", bool enabled = true)
    {
        var controller = new MatrixAppServiceController(_processor, Options.Create(new MatrixOptions
        {
            Enabled = enabled,
            HomeserverUrl = "http://synapse:8008",
            ServerName = "matrix.bee-app.tech",
            EnvironmentPrefix = "dev",
            AppServiceId = "bee-appservice-dev",
            SenderLocalpart = "bee-dev",
            AsToken = "as-token-value",
            HsToken = HsToken,
        }), NullLogger<MatrixAppServiceController>.Instance);

        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (authorization is not null) http.Request.Headers.Authorization = authorization;
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        return controller;
    }

    [Fact]
    public async Task A_valid_token_is_accepted_and_the_transaction_processed()
    {
        var result = await Build($"Bearer {HsToken}", """{"events":[{"event_id":"$a"}]}""").Transaction("txn1", default);

        Assert.IsType<OkObjectResult>(result);
        await _processor.Received(1).ProcessAsync(Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer wrong-token")]
    [InlineData("Bearer as-token-value")]   // the OUTBOUND token must not authenticate inbound
    [InlineData("Basic aHM6dG9rZW4=")]
    [InlineData("hs-token-value")]          // no Bearer scheme
    public async Task Anything_but_the_hs_token_is_rejected_and_never_processed(string? authorization)
    {
        var result = await Build(authorization).Transaction("txn1", default);

        Assert.IsType<UnauthorizedObjectResult>(result);
        await _processor.DidNotReceiveWithAnyArgs().ProcessAsync(default, default);
    }

    [Fact]
    public async Task Presenting_the_as_token_is_rejected()
    {
        // The two tokens travel in opposite directions. If the outbound as_token were accepted
        // here, anyone who compromised it could also forge transcript entries.
        var result = await Build("Bearer as-token-value").Transaction("txn1", default);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Malformed_json_is_rejected_with_400_rather_than_looped_forever()
    {
        // It will never parse on retry, so making Synapse redeliver it is pure queue-blocking.
        var result = await Build($"Bearer {HsToken}", "{not json").Transaction("txn1", default);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("M_NOT_JSON", JsonSerializer.Serialize(bad.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_matrix_disabled_the_transaction_is_acknowledged_not_refused()
    {
        // A 503 here would be retried forever and stall the homeserver's queue for this
        // appservice, so a feature flag would turn into a Synapse-side backlog.
        var result = await Build($"Bearer {HsToken}", enabled: false).Transaction("txn1", default);

        Assert.IsType<OkObjectResult>(result);
        await _processor.DidNotReceiveWithAnyArgs().ProcessAsync(default, default);
    }

    [Fact]
    public void User_and_room_queries_answer_M_NOT_FOUND_when_authenticated()
    {
        // Users and rooms are provisioned eagerly, so "not found" is the honest answer. Answering
        // properly keeps Synapse's logs quiet.
        var controller = Build($"Bearer {HsToken}");

        Assert.IsType<NotFoundObjectResult>(controller.QueryUser("@bee_u_dev_x:matrix.bee-app.tech"));
        Assert.IsType<NotFoundObjectResult>(controller.QueryRoom("#booking-dev-x:matrix.bee-app.tech"));
    }

    [Fact]
    public void User_and_room_queries_still_require_the_token()
    {
        // Otherwise they become an unauthenticated probe for which localparts exist.
        var controller = Build("Bearer nope");

        Assert.IsType<UnauthorizedObjectResult>(controller.QueryUser("@x:m"));
        Assert.IsType<UnauthorizedObjectResult>(controller.QueryRoom("#x:m"));
    }
}
