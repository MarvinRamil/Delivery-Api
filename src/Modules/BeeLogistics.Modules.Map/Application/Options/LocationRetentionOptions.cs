using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Map.Application;

/// <summary>
/// Retention for the driver location trail (<c>map."LocationHistory"</c>).
///
/// The table had no retention at all: a single actively-tracking driver writes roughly 1,200 rows
/// an hour, accumulating permanently on a table that also carries a PostGIS geometry column and
/// three indexes (GitLab #39).
/// </summary>
public class LocationRetentionOptions
{
    public const string SectionName = "Location:Retention";

    /// <summary>
    /// Whether old trail points are deleted. On by default, with a switch because this deletes data.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long a trail point is kept.
    ///
    /// 60 days covers operational debugging - "where was this driver during that booking" - with
    /// room for a dispute raised well after the delivery. How long delivery evidence must be kept is
    /// a business and legal call rather than a technical one, so it is configuration rather than a
    /// constant.
    /// </summary>
    public int RetentionDays { get; set; } = 60;

    /// <summary>
    /// Ceiling on rows deleted per run, so one execution cannot hold locks on the highest-volume
    /// table in the schema indefinitely. A backlog drains over successive runs.
    /// </summary>
    public int MaxRowsPerRun { get; set; } = 100_000;

    /// <summary>
    /// Rows per DELETE statement within a run. Smaller batches release locks more often at the cost
    /// of more round trips.
    /// </summary>
    public int BatchSize { get; set; } = 5_000;
}

/// <summary>
/// Validates <see cref="LocationRetentionOptions"/> at startup (fail fast), matching the payment
/// options validators.
/// </summary>
public class LocationRetentionOptionsValidator : IValidateOptions<LocationRetentionOptions>
{
    /// <summary>
    /// Below this the job stops being retention and starts being live-data deletion. A one-day
    /// window would erase the trail for bookings still in dispute.
    /// </summary>
    private const int MinimumRetentionDays = 7;

    public ValidateOptionsResult Validate(string? name, LocationRetentionOptions options)
    {
        if (options.Enabled && options.RetentionDays < MinimumRetentionDays)
            return ValidateOptionsResult.Fail(
                $"{LocationRetentionOptions.SectionName}:{nameof(LocationRetentionOptions.RetentionDays)} " +
                $"must be at least {MinimumRetentionDays}. To stop deleting trail data entirely, set " +
                $"{nameof(LocationRetentionOptions.Enabled)} to false.");

        if (options.MaxRowsPerRun < 1)
            return ValidateOptionsResult.Fail(
                $"{LocationRetentionOptions.SectionName}:{nameof(LocationRetentionOptions.MaxRowsPerRun)} must be >= 1.");

        if (options.BatchSize < 1)
            return ValidateOptionsResult.Fail(
                $"{LocationRetentionOptions.SectionName}:{nameof(LocationRetentionOptions.BatchSize)} must be >= 1.");

        return ValidateOptionsResult.Success;
    }
}
