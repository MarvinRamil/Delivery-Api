using BeeLogistics.Modules.Notification.Application.Commands;
using BeeLogistics.Modules.Notification.Application.DTOs;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Domain;
using BeeLogistics.Modules.Notification.Infrastructure;
using BeeLogistics.Modules.Notification.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace BeeLogistics.Tests.Notifications;

/// <summary>
/// Registering a device token (issue #94).
///
/// <para>
/// The unique index is on <c>Token</c> alone. Two things broke against it: the lookup guarding the
/// insert filtered on <c>IsActive</c> and so could not see a deactivated row, and nothing handled a
/// unique violation when one slipped through. The first is a <b>permanent</b> failure, not a race —
/// a device whose token was ever deactivated could never register again.
/// </para>
/// </summary>
public class DeviceTokenRegistrationTests
{
    private const string Token = "ExponentPushToken[abc123]";
    private const string UserId = "user-1";

    private readonly IDeviceTokenRepository _repo = Substitute.For<IDeviceTokenRepository>();
    private readonly IPushProviderPolicy _policy = Substitute.For<IPushProviderPolicy>();

    private RegisterDeviceTokenCommandHandler Handler() => new(_repo, _policy);

    private static RegisterDeviceTokenCommand Command() =>
        new(UserId, new RegisterDeviceTokenDto(Token, "android", "driver", "expo"));

    private static DeviceToken Existing(bool active)
    {
        var t = new DeviceToken("someone-else", Token, "ios", "customer", "fcm");
        t.IsActive = active;
        return t;
    }

    [Fact]
    public async Task A_deactivated_token_is_reactivated_rather_than_re_inserted()
    {
        // THE bug. The lookup used to filter IsActive while the unique index does not, so this row
        // was invisible: the handler took the create path and the INSERT collided every single
        // time. That device could never register again.
        var deactivated = Existing(active: false);
        _repo.GetByTokenAsync(Token, Arg.Any<CancellationToken>()).Returns(deactivated);

        var result = await Handler().Handle(Command(), default);

        Assert.True(result.IsSuccess);
        Assert.True(deactivated.IsActive);
        Assert.Equal(UserId, deactivated.UserId);
        // Nothing new inserted — the row that would have collided is the one we updated.
        await _repo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task A_token_that_moved_to_another_user_is_reassigned()
    {
        // A phone changing hands, or a reinstall under a different account. The token is unique to
        // the device, so the newest registration owns it.
        var existing = Existing(active: true);
        _repo.GetByTokenAsync(Token, Arg.Any<CancellationToken>()).Returns(existing);

        await Handler().Handle(Command(), default);

        Assert.Equal(UserId, existing.UserId);
        Assert.Equal("android", existing.Platform);
        Assert.Equal("driver", existing.AppType);
    }

    [Fact]
    public async Task A_brand_new_token_is_inserted()
    {
        _repo.GetByTokenAsync(Token, Arg.Any<CancellationToken>()).Returns((DeviceToken?)null);

        var result = await Handler().Handle(Command(), default);

        Assert.True(result.IsSuccess);
        await _repo.Received(1).AddAsync(Arg.Is<DeviceToken>(t => t.Token == Token), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_concurrent_insert_resolves_to_the_winner_instead_of_throwing()
    {
        // The app registers on launch and again on token refresh, so two in flight at once is
        // ordinary. The loser must adopt the winner's row, not surface a 500.
        var winner = Existing(active: true);
        _repo.GetByTokenAsync(Token, Arg.Any<CancellationToken>())
            .Returns(_ => null, _ => winner);   // absent on the guard, present after the collision
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => throw new DbUpdateException("23505 IX_DeviceTokens_Token"), _ => Task.CompletedTask);

        var result = await Handler().Handle(Command(), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(UserId, winner.UserId);
        Assert.True(winner.IsActive);
        await _repo.Received(1).UpdateAsync(winner, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_write_failure_that_is_not_a_collision_still_surfaces()
    {
        // Nothing to adopt. Swallowing this would report success for a token that was never stored,
        // and the device would silently stop receiving pushes.
        _repo.GetByTokenAsync(Token, Arg.Any<CancellationToken>()).Returns((DeviceToken?)null);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException("disk full"));

        await Assert.ThrowsAsync<DbUpdateException>(() => Handler().Handle(Command(), default));
    }
}

/// <summary>
/// The repository query that guards the insert (issue #94).
///
/// <para>
/// These run against a real context rather than a substitute on purpose. The defect lived in the
/// query itself, so a test that mocks <c>IDeviceTokenRepository</c> cannot see it — and one did
/// not: restoring the <c>IsActive</c> filter left the handler tests green.
/// </para>
/// </summary>
public class DeviceTokenLookupTests
{
    private const string Token = "ExponentPushToken[abc123]";

    private static NotificationDbContext NewDb(params DeviceToken[] seed)
    {
        var options = new DbContextOptionsBuilder<NotificationDbContext>()
            .UseInMemoryDatabase($"device-tokens-{Guid.NewGuid()}")
            .Options;
        var db = new NotificationDbContext(options);
        if (seed.Length > 0)
        {
            db.DeviceTokens.AddRange(seed);
            db.SaveChanges();
        }
        return db;
    }

    private static DeviceToken TokenRow(bool active)
    {
        var t = new DeviceToken("user-1", Token, "android", "driver", "expo");
        t.IsActive = active;
        return t;
    }

    [Fact]
    public async Task A_deactivated_token_is_still_found()
    {
        // The unique index is on Token alone, so the lookup that guards the insert must match it.
        // Filtering IsActive here made this row invisible and the INSERT collided every time — a
        // device whose token was ever deactivated could never register again.
        var repo = new DeviceTokenRepository(NewDb(TokenRow(active: false)));

        var found = await repo.GetByTokenAsync(Token);

        Assert.NotNull(found);
        Assert.False(found!.IsActive);
    }

    [Fact]
    public async Task An_active_token_is_found()
    {
        var repo = new DeviceTokenRepository(NewDb(TokenRow(active: true)));
        Assert.NotNull(await repo.GetByTokenAsync(Token));
    }

    [Fact]
    public async Task An_absent_token_returns_null()
    {
        var repo = new DeviceTokenRepository(NewDb());
        Assert.Null(await repo.GetByTokenAsync(Token));
    }

    [Fact]
    public async Task Sending_still_excludes_inactive_tokens()
    {
        // GetByTokensAsync is for delivery, not for guarding an insert — it SHOULD skip inactive
        // rows, and dropping the filter there would push to devices that were deliberately retired.
        var repo = new DeviceTokenRepository(NewDb(TokenRow(active: false)));

        Assert.Empty(await repo.GetByTokensAsync(new[] { Token }));
    }
}
