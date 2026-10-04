using BeeLogistics.Modules.Drivers.Domain;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// DriverPackageInsurancePolicy (issue #104): recurring annual coverage, tracked as a sequential
/// paid-through-year counter anchored to the driver's first payment, not a calendar year.
/// </summary>
public class DriverPackageInsurancePolicyTests
{
    [Fact]
    public void A_new_policy_is_not_enrolled_and_unpaid()
    {
        var policy = new DriverPackageInsurancePolicy(Guid.NewGuid());

        Assert.Equal(DriverPackageInsurancePolicyStatus.NotEnrolled, policy.Status);
        Assert.Equal(0, policy.PaidThroughYearNumber);
        Assert.Null(policy.CoverageStartDate);
        Assert.Null(policy.CoverageEndDate);
    }

    [Fact]
    public void Paying_year_one_anchors_the_coverage_start_date_and_activates_the_policy()
    {
        var policy = new DriverPackageInsurancePolicy(Guid.NewGuid());
        var paidAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        policy.MarkYearPaid(1, paidAt);

        Assert.Equal(DriverPackageInsurancePolicyStatus.Active, policy.Status);
        Assert.Equal(1, policy.PaidThroughYearNumber);
        Assert.Equal(paidAt, policy.CoverageStartDate);
        Assert.Equal(paidAt.AddYears(1), policy.CoverageEndDate);
    }

    [Fact]
    public void Paying_a_second_year_does_not_move_the_original_coverage_start_date()
    {
        var policy = new DriverPackageInsurancePolicy(Guid.NewGuid());
        var firstPaidAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var secondPaidAt = new DateTime(2027, 3, 5, 0, 0, 0, DateTimeKind.Utc);

        policy.MarkYearPaid(1, firstPaidAt);
        policy.MarkYearPaid(2, secondPaidAt);

        Assert.Equal(2, policy.PaidThroughYearNumber);
        Assert.Equal(firstPaidAt, policy.CoverageStartDate);
        Assert.Equal(firstPaidAt.AddYears(2), policy.CoverageEndDate);
    }

    [Fact]
    public void Settling_a_year_out_of_order_is_refused()
    {
        var policy = new DriverPackageInsurancePolicy(Guid.NewGuid());
        var now = DateTime.UtcNow;

        // Skipping straight to year 2 without year 1.
        Assert.Throws<InvalidOperationException>(() => policy.MarkYearPaid(2, now));
    }

    [Fact]
    public void Settling_the_same_year_twice_is_refused()
    {
        var policy = new DriverPackageInsurancePolicy(Guid.NewGuid());
        var now = DateTime.UtcNow;
        policy.MarkYearPaid(1, now);

        Assert.Throws<InvalidOperationException>(() => policy.MarkYearPaid(1, now));
    }

    [Fact]
    public void Marking_lapsed_only_applies_to_an_active_policy()
    {
        var policy = new DriverPackageInsurancePolicy(Guid.NewGuid());

        // Not enrolled yet — nothing to lapse.
        policy.MarkLapsed();
        Assert.Equal(DriverPackageInsurancePolicyStatus.NotEnrolled, policy.Status);

        policy.MarkYearPaid(1, DateTime.UtcNow);
        policy.MarkLapsed();
        Assert.Equal(DriverPackageInsurancePolicyStatus.Lapsed, policy.Status);

        // Idempotent: lapsing an already-lapsed policy is a no-op, not an error.
        policy.MarkLapsed();
        Assert.Equal(DriverPackageInsurancePolicyStatus.Lapsed, policy.Status);
    }
}
