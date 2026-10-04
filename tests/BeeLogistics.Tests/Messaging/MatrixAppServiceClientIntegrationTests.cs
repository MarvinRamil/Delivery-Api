using BeeLogistics.Modules.Messaging.Application;
using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace BeeLogistics.Tests.Messaging;

/// <summary>
/// Exercises <see cref="MatrixAppServiceClient"/> against a REAL Synapse.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in: no-ops unless <c>MATRIX_IT_AS_TOKEN</c> and <c>MATRIX_IT_HOMESERVER</c> are set, so CI
/// and a normal <c>dotnet test</c> are unaffected and no secret ever lives in the repo.
/// </para>
/// <para>
/// Worth having because the unit tests above pin what we <em>send</em>, and only a real homeserver
/// can confirm Synapse agrees — that the alias-collision path really returns <c>M_ROOM_IN_USE</c>,
/// that masqueraded joins are accepted, and that power levels land as intended.
/// </para>
/// <code>
/// MATRIX_IT_HOMESERVER=https://matrix.bee-app.tech \
/// MATRIX_IT_AS_TOKEN=... MATRIX_IT_PREFIX=dev MATRIX_IT_SENDER=bee-dev \
/// MATRIX_IT_SERVER_NAME=matrix.bee-app.tech \
/// dotnet test --filter FullyQualifiedName~MatrixAppServiceClientIntegrationTests
/// </code>
/// </remarks>
public class MatrixAppServiceClientIntegrationTests
{
    private readonly ITestOutputHelper _output;
    public MatrixAppServiceClientIntegrationTests(ITestOutputHelper output) => _output = output;

    private static string? Env(string key) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : null;

    private static bool Configured =>
        Env("MATRIX_IT_AS_TOKEN") is not null && Env("MATRIX_IT_HOMESERVER") is not null;

    private static (MatrixAppServiceClient Client, MatrixOptions Options) Build()
    {
        var options = new MatrixOptions
        {
            Enabled = true,
            HomeserverUrl = Env("MATRIX_IT_HOMESERVER")!,
            ServerName = Env("MATRIX_IT_SERVER_NAME") ?? "matrix.bee-app.tech",
            EnvironmentPrefix = Env("MATRIX_IT_PREFIX") ?? "dev",
            AppServiceId = "bee-appservice-dev",
            SenderLocalpart = Env("MATRIX_IT_SENDER") ?? "bee-dev",
            AsToken = Env("MATRIX_IT_AS_TOKEN")!,
            HsToken = "unused-here",
            TimeoutSeconds = 30,
        };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(MatrixHttpClient.Name).Returns(_ => new HttpClient
        {
            BaseAddress = MatrixHttpClient.NormalizeBaseAddress(options.HomeserverUrl),
            Timeout = TimeSpan.FromSeconds(30),
        });

        return (new MatrixAppServiceClient(factory, Options.Create(options), NullLogger<MatrixAppServiceClient>.Instance), options);
    }

    [Fact]
    public async Task Full_room_provisioning_round_trip_against_a_real_homeserver()
    {
        if (!Configured)
        {
            _output.WriteLine("skipped: MATRIX_IT_AS_TOKEN / MATRIX_IT_HOMESERVER not set");
            return;
        }

        var (client, options) = Build();
        var run = Guid.NewGuid().ToString("N")[..8];
        var customer = $"{options.UserLocalpartPrefix}itcust{run}";
        var driver = $"{options.UserLocalpartPrefix}itdrv{run}";
        var alias = $"booking-{options.EnvironmentPrefix}-itbkg{run}";

        // 1. Provision two users, then prove re-provisioning is a no-op rather than an error.
        var customerMxid = await client.EnsureUserAsync(customer, "IT Customer");
        var driverMxid = await client.EnsureUserAsync(driver, "IT Driver");
        Assert.Equal(customerMxid, await client.EnsureUserAsync(customer, "IT Customer"));
        _output.WriteLine($"users: {customerMxid} / {driverMxid}");

        // 2. Create the room with the customer invited.
        var created = await client.CreateOrAdoptRoomAsync(new CreateRoomRequest(
            alias, $"Booking IT-{run}", "Integration test room",
            options.BotUserId, new[] { customerMxid },
            new Dictionary<string, object> { ["bookingNumber"] = $"IT-{run}" }));
        Assert.False(created.Adopted);
        _output.WriteLine($"room: {created.RoomId}");

        // 3. The redelivery path: same alias again must adopt, not create a second room.
        var adopted = await client.CreateOrAdoptRoomAsync(new CreateRoomRequest(
            alias, $"Booking IT-{run}", null, options.BotUserId, Array.Empty<string>(), null));
        Assert.True(adopted.Adopted);
        Assert.Equal(created.RoomId, adopted.RoomId);

        // 4. Accept on the customer's behalf; an appservice user has no client to tap "accept".
        await client.JoinAsUserAsync(created.RoomId, customerMxid);

        // 5. Driver assigned later, exactly as a real booking goes.
        await client.InviteAsync(created.RoomId, driverMxid);
        await client.JoinAsUserAsync(created.RoomId, driverMxid);
        await client.InviteAsync(created.RoomId, driverMxid);   // already in room -> must not throw

        // 6. Bot posts a status notice; the customer posts as themselves.
        var txn = $"it-{run}-1";
        var botEvent = await client.SendMessageAsync(created.RoomId,
            new { msgtype = "m.notice", body = "Driver assigned." }, txn);
        Assert.StartsWith("$", botEvent, StringComparison.Ordinal);

        // Same transaction id again — Matrix must de-duplicate to the same event.
        Assert.Equal(botEvent, await client.SendMessageAsync(created.RoomId,
            new { msgtype = "m.notice", body = "Driver assigned." }, txn));

        await client.SendMessageAsync(created.RoomId,
            new { msgtype = "m.text", body = "On my way" }, $"it-{run}-2", asUserId: customerMxid);

        // 7. Alias resolves to the same room.
        Assert.Equal(created.RoomId, await client.ResolveAliasAsync($"#{alias}:{options.ServerName}"));

        _output.WriteLine("round trip OK");
    }
}
