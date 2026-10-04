namespace BeeLogistics.Modules.Accounting.Domain;

/// <summary>
/// Fast, denormalized record of a completed sale (cash or cashless) for reporting.
/// Append-only; one row per booking completion.
/// </summary>
public class SalesEntry
{
    public Guid Id { get; private set; }
    public Guid BookingId { get; private set; }
    public DateTime CompletedAtUtc { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "PHP";
    public string PaymentMethod { get; private set; } = null!; // "Cash" or "PayOnline"
    public Guid DriverId { get; private set; }
    public Guid CustomerId { get; private set; }
    public decimal PlatformCommissionAmount { get; private set; }
    public decimal DriverAmount { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private SalesEntry() { }

    public static SalesEntry Create(
        Guid bookingId,
        DateTime completedAtUtc,
        decimal amount,
        string currency,
        string paymentMethod,
        Guid driverId,
        Guid customerId,
        decimal platformCommissionAmount,
        decimal driverAmount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));
        if (string.IsNullOrWhiteSpace(paymentMethod))
            throw new ArgumentException("Payment method is required", nameof(paymentMethod));

        return new SalesEntry
        {
            Id = Guid.NewGuid(),
            BookingId = bookingId,
            CompletedAtUtc = completedAtUtc,
            Amount = amount,
            Currency = currency ?? "PHP",
            PaymentMethod = paymentMethod.Trim(),
            DriverId = driverId,
            CustomerId = customerId,
            PlatformCommissionAmount = platformCommissionAmount,
            DriverAmount = driverAmount,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
