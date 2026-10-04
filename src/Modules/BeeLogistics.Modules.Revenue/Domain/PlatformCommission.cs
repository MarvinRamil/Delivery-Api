using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Revenue.Domain;

/// <summary>
/// Ledger entry recording the platform's commission on each completed delivery.
/// One record per booking — immutable once created.
/// </summary>
public class PlatformCommission : Entity
{
    public Guid BookingId { get; private set; }
    public Guid DriverId { get; private set; }
    public string PaymentMethod { get; private set; } = string.Empty; // Cash, BankTransfer, EWallet, etc.
    public decimal GrossAmount { get; private set; }   // Total fare (payment amount)
    public decimal CommissionRate { get; private set; } // e.g. 0.05 = 5%
    public decimal CommissionAmount { get; private set; }
    public decimal DriverAmount { get; private set; }
    public PlatformCommissionStatus Status { get; private set; }

    private PlatformCommission() { } // EF Core

    public PlatformCommission(
        Guid bookingId,
        Guid driverId,
        string paymentMethod,
        decimal grossAmount,
        decimal commissionRate)
    {
        if (grossAmount <= 0)
            throw new ArgumentException("Gross amount must be positive", nameof(grossAmount));
        if (commissionRate < 0 || commissionRate > 1)
            throw new ArgumentException("Commission rate must be between 0 and 1", nameof(commissionRate));

        // Delegated to EarningsSplit so the wallet-crediting consumers compute the driver's
        // share from the same definition rather than their own copy of the arithmetic.
        var split = EarningsSplit.For(grossAmount, commissionRate);

        BookingId = bookingId;
        DriverId = driverId;
        PaymentMethod = paymentMethod;
        GrossAmount = grossAmount;
        CommissionRate = commissionRate;
        CommissionAmount = split.CommissionAmount;
        DriverAmount = split.DriverAmount;
        Status = PlatformCommissionStatus.Recorded;
        CreatedAt = DateTime.UtcNow;
    }

    public void MarkReconciled()
    {
        Status = PlatformCommissionStatus.Reconciled;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// The booking's payment was refunded. Idempotent - a redelivered refund event must not throw
    /// or double-apply anything, since this record carries no amount to unwind twice.
    /// </summary>
    public void MarkReversed()
    {
        if (Status == PlatformCommissionStatus.Reversed)
            return;

        Status = PlatformCommissionStatus.Reversed;
        UpdatedAt = DateTime.UtcNow;
    }
}

public enum PlatformCommissionStatus
{
    Recorded = 0,
    Reconciled = 1,
    Reversed = 2
}
