using BeeLogistics.Modules.Map.Application;
using BeeLogistics.Modules.Map.Application.Interfaces;
using BeeLogistics.Modules.Map.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Map;

/// <summary>
/// Retention for the driver location trail (GitLab #39).
///
/// <c>map."LocationHistory"</c> had no retention at all: an actively-tracking driver writes roughly
/// 1,200 rows an hour and nothing ever removed them. Since this deletes data, the tests weigh
/// toward the bounds - what it must not delete, and how much it may do in one run.
/// </summary>
public class LocationHistoryRetentionTests
{
    private readonly ILocationRepository _repository = Substitute.For<ILocationRepository>();

    private static readonly DateTime Now = new(2026, 8, 2, 3, 30, 0, DateTimeKind.Utc);

    private LocationHistoryRetentionService Service(
        bool enabled = true, int retentionDays = 60, int maxRowsPerRun = 100_000, int batchSize = 5_000) => new(
        _repository,
        Options.Create(new LocationRetentionOptions
        {
            Enabled = enabled,
            RetentionDays = retentionDays,
            MaxRowsPerRun = maxRowsPerRun,
            BatchSize = batchSize
        }),
        NullLogger<LocationHistoryRetentionService>.Instance);

    /// <summary>Reports <paramref name="available"/> rows as deletable, then nothing.</summary>
    private void RepositoryHas(int available)
    {
        var remaining = available;
        _repository.DeleteLocationHistoryOlderThanAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var requested = ci.ArgAt<int>(1);
                var deleted = Math.Min(requested, remaining);
                remaining -= deleted;
                return deleted;
            });
    }

    [Fact]
    public async Task Deletes_using_the_configured_retention_window()
    {
        RepositoryHas(10);

        await Service(retentionDays: 30).PurgeAsync(Now);

        await _repository.Received().DeleteLocationHistoryOlderThanAsync(
            Now.AddDays(-30), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_the_number_deleted()
    {
        RepositoryHas(1_234);

        var deleted = await Service().PurgeAsync(Now);

        Assert.Equal(1_234, deleted);
    }

    /// <summary>
    /// A short batch means the backlog is drained; continuing would issue pointless DELETEs against
    /// the busiest table in the schema.
    /// </summary>
    [Fact]
    public async Task Stops_as_soon_as_a_batch_comes_back_short()
    {
        RepositoryHas(100);

        await Service(batchSize: 5_000).PurgeAsync(Now);

        await _repository.Received(1).DeleteLocationHistoryOlderThanAsync(
            Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deletes_nothing_when_there_is_nothing_old_enough()
    {
        RepositoryHas(0);

        var deleted = await Service().PurgeAsync(Now);

        Assert.Equal(0, deleted);
    }

    /// <summary>
    /// One run must not delete unboundedly: this is a bulk delete on a table the live ingest path
    /// is writing to continuously, so a large backlog drains across successive runs instead.
    /// </summary>
    [Fact]
    public async Task Never_exceeds_the_per_run_ceiling()
    {
        RepositoryHas(1_000_000);

        var deleted = await Service(maxRowsPerRun: 10_000, batchSize: 5_000).PurgeAsync(Now);

        Assert.Equal(10_000, deleted);
        await _repository.Received(2).DeleteLocationHistoryOlderThanAsync(
            Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The final batch is trimmed so the ceiling is not overshot.</summary>
    [Fact]
    public async Task Trims_the_last_batch_to_the_remaining_allowance()
    {
        RepositoryHas(1_000_000);

        await Service(maxRowsPerRun: 7_000, batchSize: 5_000).PurgeAsync(Now);

        await _repository.Received(1).DeleteLocationHistoryOlderThanAsync(
            Arg.Any<DateTime>(), 5_000, Arg.Any<CancellationToken>());
        await _repository.Received(1).DeleteLocationHistoryOlderThanAsync(
            Arg.Any<DateTime>(), 2_000, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Kill_switch_deletes_nothing()
    {
        RepositoryHas(1_000_000);

        var deleted = await Service(enabled: false).PurgeAsync(Now);

        Assert.Equal(0, deleted);
        await _repository.DidNotReceive().DeleteLocationHistoryOlderThanAsync(
            Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stops_when_cancelled()
    {
        RepositoryHas(1_000_000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var deleted = await Service(maxRowsPerRun: 1_000_000).PurgeAsync(Now, cts.Token);

        Assert.Equal(0, deleted);
    }

    // ---- Options validation -------------------------------------------------------------------

    /// <summary>
    /// A one-day window would erase the trail for bookings still in dispute. Caught at startup
    /// rather than discovered after the data is gone.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public void Validator_rejects_a_dangerously_short_retention_window(int days)
    {
        var result = new LocationRetentionOptionsValidator().Validate(null, new LocationRetentionOptions
        {
            Enabled = true,
            RetentionDays = days
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validator_allows_any_window_when_retention_is_disabled()
    {
        var result = new LocationRetentionOptionsValidator().Validate(null, new LocationRetentionOptions
        {
            Enabled = false,
            RetentionDays = 0
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validator_accepts_the_defaults()
    {
        Assert.True(new LocationRetentionOptionsValidator().Validate(null, new LocationRetentionOptions()).Succeeded);
    }

    /// <summary>
    /// An environment that configures nothing must still land on the safe values, so forgetting to
    /// set these cannot silently produce an aggressive deletion window.
    /// </summary>
    [Fact]
    public void Absent_configuration_binds_to_the_safe_defaults()
    {
        var options = new LocationRetentionOptions();

        Assert.True(options.Enabled);
        Assert.Equal(60, options.RetentionDays);
        Assert.Equal(100_000, options.MaxRowsPerRun);
        Assert.Equal(5_000, options.BatchSize);
    }
}
