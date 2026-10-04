using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Domain;

/// <summary>
/// One driver's standing on their package-insurance coverage (issue #104) — coverage for the
/// packages they carry, paid annually, priced by vehicle type. Not vehicle/liability insurance —
/// see <see cref="DriverApplication.InsurancePath"/> for that, a separate, unrelated document.
/// </summary>
/// <remarks>
/// A dedicated aggregate rather than fields on <see cref="DriverWallet"/> or derived purely from
/// <see cref="WalletTransaction"/> history: <c>xmin</c> here is what makes two concurrent
/// settlements of the same policy year fail safely (one throws
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> instead of both
/// silently succeeding) — <c>WalletTransaction</c> carries no row-version of its own, so nothing
/// would otherwise serialize that race. Kept separate from <c>DriverWallet</c> so this feature's
/// writes don't add contention to that already-busy aggregate; the premium settles to the
/// platform wallet and never touches <c>Balance</c>/<c>TopUpBalance</c> either way.
/// </remarks>
public class DriverPackageInsurancePolicy : Entity
{
    public Guid DriverId { get; private set; }

    /// <summary>Set once, on the first successful premium payment. Anchors every later due date.</summary>
    public DateTime? CoverageStartDate { get; private set; }

    /// <summary>0 = never paid; N = years 1..N are covered. Advances by exactly one per settlement.</summary>
    public int PaidThroughYearNumber { get; private set; }

    public DriverPackageInsurancePolicyStatus Status { get; private set; } = DriverPackageInsurancePolicyStatus.NotEnrolled;

    /// <summary>Postgres xmin mapping — mirrors <see cref="DriverWallet.Version"/>.</summary>
    public uint Version { get; private set; }

    private DriverPackageInsurancePolicy() { } // For EF Core

    public DriverPackageInsurancePolicy(Guid driverId)
    {
        Id = Guid.NewGuid();
        DriverId = driverId;
        PaidThroughYearNumber = 0;
        Status = DriverPackageInsurancePolicyStatus.NotEnrolled;
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>Derived, not stored: the year the current coverage lapses if not renewed.</summary>
    public DateTime? CoverageEndDate => CoverageStartDate?.AddYears(PaidThroughYearNumber);

    /// <summary>
    /// Records a settled premium payment for <paramref name="yearNumber"/>. Strictly sequential —
    /// a driver always pays for exactly the next unpaid year, never skips or pays ahead.
    /// </summary>
    public void MarkYearPaid(int yearNumber, DateTime paidAtUtc)
    {
        if (yearNumber != PaidThroughYearNumber + 1)
            throw new InvalidOperationException(
                $"Cannot settle year {yearNumber}; policy is paid through year {PaidThroughYearNumber}.");

        if (CoverageStartDate is null)
            CoverageStartDate = paidAtUtc;

        PaidThroughYearNumber = yearNumber;
        Status = DriverPackageInsurancePolicyStatus.Active;
        Touch();
    }

    /// <summary>Flips an expired-but-unrenewed policy to Lapsed. No-op if already lapsed.</summary>
    public void MarkLapsed()
    {
        if (Status != DriverPackageInsurancePolicyStatus.Active)
            return;

        Status = DriverPackageInsurancePolicyStatus.Lapsed;
        Touch();
    }

    private void Touch() => UpdatedAt = DateTime.UtcNow;
}

public enum DriverPackageInsurancePolicyStatus
{
    NotEnrolled = 0,
    Active = 1,
    Lapsed = 2
}
