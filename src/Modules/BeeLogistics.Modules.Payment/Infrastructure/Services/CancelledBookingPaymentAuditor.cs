using BeeLogistics.Modules.Payment.Application;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Application.Services;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Infrastructure;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Payment.Infrastructure.Services;

/// <inheritdoc cref="ICancelledBookingPaymentAuditor"/>
public sealed class CancelledBookingPaymentAuditor : ICancelledBookingPaymentAuditor
{
    /// <summary>Matches the other reconciliation warnings: enough IDs to act on, not a wall of them.</summary>
    private const int MaxIdsLogged = 20;

    private readonly IPaymentRepository _paymentRepository;
    private readonly IMediator _mediator;
    private readonly PaymentReconciliationOptions _options;
    private readonly ILogger<CancelledBookingPaymentAuditor> _logger;

    public CancelledBookingPaymentAuditor(
        IPaymentRepository paymentRepository,
        IMediator mediator,
        IOptions<PaymentReconciliationOptions> options,
        ILogger<CancelledBookingPaymentAuditor> logger)
    {
        _paymentRepository = paymentRepository;
        _mediator = mediator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> AuditAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var since = nowUtc.AddHours(-_options.CancelledBookingAuditLookbackHours);

        // Bookings owns booking status; this module cannot join on it. See GetCancelledBookingsSinceQuery.
        var cancelled = await _mediator.Send(
            new GetCancelledBookingsSinceQuery(since, _options.MaxCancelledBookingsAuditedPerRun), ct);

        if (!cancelled.IsSuccess || cancelled.Value is null)
        {
            _logger.LogWarning(
                "[PaymentReconciliation] Could not read cancelled bookings for the refund audit: {Error}",
                cancelled.Error);
            return 0;
        }

        var bookingIds = cancelled.Value.Select(b => b.BookingId).ToList();
        if (bookingIds.Count == 0)
            return 0;

        var payments = await _paymentRepository.GetByBookingIdsAsync(bookingIds, ct);

        // Still Paid means the money never went back. Refunded and RefundPending are resolved or in
        // flight; Cash never came through a gateway and is settled off-platform.
        var unrefunded = payments
            .Where(p => p.Status == PaymentStatus.Paid && p.Method != PaymentMethod.Cash)
            .ToList();

        if (unrefunded.Count == 0)
            return 0;

        BeeMetrics.PaymentsCancelledUnrefunded.Add(
            unrefunded.Count, new KeyValuePair<string, object?>("source", "sweep"));

        _logger.LogWarning(
            "[PaymentReconciliation] {Count} cancelled booking(s) in the last {Hours}h still hold a paid online payment totalling {Total:0.00} (customer cancelled or nobody accepted, money not returned). Booking IDs: {BookingIds}",
            unrefunded.Count,
            _options.CancelledBookingAuditLookbackHours,
            unrefunded.Sum(p => p.RefundableAmount),
            string.Join(", ", unrefunded.Take(MaxIdsLogged).Select(p => p.BookingId)));

        return unrefunded.Count;
    }
}
