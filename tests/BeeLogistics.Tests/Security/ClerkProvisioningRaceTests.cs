using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Identity.Infrastructure;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace BeeLogistics.Tests.Security;

/// <summary>
/// Concurrent just-in-time provisioning of a Clerk user (issue #92).
///
/// <para>
/// <c>UserManager.CreateAsync</c> queries for a duplicate email and username before inserting, so a
/// concurrent request can slip in between that check and the INSERT. The driver app fires several
/// <c>/api/auth/me</c> calls on startup, which makes that race routine for a first-time user rather
/// than exotic.
/// </para>
/// <para>
/// Identity reports only the polite version of the failure — validators catching it — as a failed
/// <c>IdentityResult</c>. A database-level unique violation <b>throws</b>, and that used to escape
/// as a 500 on sign-in.
/// </para>
/// </summary>
public class ClerkProvisioningRaceTests
{
    private const string ClerkId = "user_3ITw5m7gm2aIGeF8eOZIUPqNp6Y";

    private static IdentityAppDbContext NewDb(params ApplicationUser[] seed)
    {
        var options = new DbContextOptionsBuilder<IdentityAppDbContext>()
            .UseInMemoryDatabase($"clerk-race-{Guid.NewGuid()}")
            .Options;
        var db = new IdentityAppDbContext(options);
        if (seed.Length > 0)
        {
            db.Users.AddRange(seed);
            db.SaveChanges();
        }
        return db;
    }

    /// <summary>
    /// A UserManager whose <c>Users</c> is a real (async-capable) in-memory set, but whose
    /// <c>CreateAsync</c> fails the way Postgres does — by throwing rather than returning a result.
    /// In-memory EF does not enforce unique indexes, so the violation has to be injected.
    /// </summary>
    /// <param name="winnerLandsFirst">
    /// Inserted immediately before the throw, modelling the concurrent request that won. This
    /// ordering is the whole point: the row must NOT be visible on the first lookup and MUST be
    /// visible to the fallback, or the test passes without exercising the race at all.
    /// </param>
    private static UserManager<ApplicationUser> ManagerThatRacesOnInsert(
        IdentityAppDbContext db, Exception onCreate, ApplicationUser? winnerLandsFirst = null)
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var mgr = Substitute.For<UserManager<ApplicationUser>>(
            store, null, null, null, null, null, null, null, null);
        mgr.Users.Returns(_ => db.Users);
        mgr.CreateAsync(Arg.Any<ApplicationUser>()).Returns<Task<IdentityResult>>(_ =>
        {
            if (winnerLandsFirst is not null)
            {
                db.Users.Add(winnerLandsFirst);
                db.SaveChanges();
            }
            throw onCreate;
        });
        return mgr;
    }

    private static ClerkUserProvisioningService Service(UserManager<ApplicationUser> mgr) =>
        new(new HttpClient(),
            Options.Create(new ClerkSettings { SecretKey = "sk_test" }),
            mgr,
            NullLogger<ClerkUserProvisioningService>.Instance,
            Substitute.For<IMediator>());

    private static ClerkUserData Data() => new(
        ClerkUserId: ClerkId,
        Email: "juan@example.ph",
        EmailVerified: true,
        Phone: "+639170000000",
        PhoneVerified: true,
        FullName: "Juan Dela Cruz",
        Role: "Driver");

    [Fact]
    public async Task A_duplicate_key_from_the_database_returns_the_winning_row_instead_of_throwing()
    {
        // The exact production failure: 23505 on UserNameIndex, thrown out of SaveChanges.
        var winner = new ApplicationUser
        {
            Id = "winner", UserName = "juan@example.ph", Email = "juan@example.ph",
            FullName = "Juan Dela Cruz", Role = "Driver", ClerkUserId = ClerkId, IsActive = true
        };
        // Empty to begin with: our request looks up the Clerk id, finds nothing, and only then does
        // the other request's row land.
        var db = NewDb();
        var mgr = ManagerThatRacesOnInsert(db,
            new DbUpdateException("duplicate key value violates unique constraint \"UserNameIndex\""),
            winnerLandsFirst: winner);

        var result = await Service(mgr).UpsertAsync(Data());

        Assert.NotNull(result);
        Assert.Equal("winner", result!.Id);
        Assert.Equal(ClerkId, result.ClerkUserId);
    }

    [Fact]
    public async Task The_race_does_not_surface_as_an_exception_to_the_caller()
    {
        // This is what made it a 500 on /api/auth/me. Retrying appeared to "fix" it only because
        // by then the winning request had already created the row.
        var db = NewDb();
        var mgr = ManagerThatRacesOnInsert(db, new DbUpdateException("23505"),
            winnerLandsFirst: new ApplicationUser
            {
                Id = "winner", UserName = "juan@example.ph", Email = "juan@example.ph",
                FullName = "Juan", Role = "Driver", ClerkUserId = ClerkId, IsActive = true
            });

        var ex = await Record.ExceptionAsync(() => Service(mgr).UpsertAsync(Data()));

        Assert.Null(ex);
    }

    [Fact]
    public async Task A_failure_that_is_not_a_race_still_returns_null_rather_than_a_wrong_user()
    {
        // Nothing to re-resolve to. Returning null is correct — inventing a user here would hand
        // the caller someone else's identity.
        var db = NewDb();
        var mgr = ManagerThatRacesOnInsert(db, new DbUpdateException("23505"));

        var result = await Service(mgr).UpsertAsync(Data());

        Assert.Null(result);
    }
}
