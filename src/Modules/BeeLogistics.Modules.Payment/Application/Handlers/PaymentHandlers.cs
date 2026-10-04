using BeeLogistics.Modules.Payment.Application.DTOs;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Application.Validators;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.DTOs;
using BeeLogistics.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Payment.Application.Handlers;

// Queries
public record GetPaymentsQuery(int Page = 1, int PageSize = 50) : IRequest<Result<PagedResult<PaymentDto>>>;
public record GetPaymentByIdQuery(Guid Id) : IRequest<Result<PaymentDto>>;
public record GetPaymentsByCustomerQuery(Guid CustomerId) : IRequest<Result<IReadOnlyList<PaymentDto>>>;
public record GetPaymentByBookingQuery(Guid BookingId) : IRequest<Result<PaymentDto>>;

// Commands
// CallerUserId is the Identity user id from the JWT subject and IsElevated is the resolved
// backoffice policy result, both supplied by the presentation layer so the handlers can enforce
// object-level authorization without reaching for HttpContext.
/// <param name="IdempotencyKey">
/// Optional client-supplied key from the Idempotency-Key header. When repeated, the original
/// payment is returned instead of a second checkout being opened. Absent means no dedupe, so
/// existing clients are unaffected.
/// </param>
public record CreatePaymentCommand(CreatePaymentDto Dto, Guid CallerUserId, bool IsElevated, string? IdempotencyKey = null) : IRequest<Result<PaymentDto>>;
/// <param name="PaidAmount">
/// The amount the provider says was paid, when the payload carries one. Compared against the
/// stored amount before settling; null means the provider did not report one and the check is
/// skipped (the status transition still applies).
/// </param>
public record ProcessWebhookCommand(string Provider, string ProviderPaymentId, string Status, DateTime? PaidAt, decimal? PaidAmount = null) : IRequest<Result>;
public record ProcessRefundWebhookCommand(string Provider, string ProviderRefundId, string Status, string? FailureReason) : IRequest<Result>;
/// <summary>Persists the provider capture id (e.g. PayMongo pay_...) delivered in a paid webhook so refunds skip a provider round-trip.</summary>
public record SetPaymentCaptureIdCommand(string Provider, string ProviderPaymentId, string CaptureId) : IRequest<Result>;
public record LinkPaymentToBookingCommand(Guid PaymentId, Guid BookingId, Guid CallerUserId, bool IsElevated) : IRequest<Result>;
/// <param name="DriverId">
/// The driver whose earnings should be reversed if they were credited for this booking. Null means
/// no driver was ever assigned - a booking cancelled because nobody accepted it (GitLab #51). It is
/// nullable rather than Guid.Empty so the validator can still insist a caller who names a driver
/// names a real one; see RefundPaymentCommandValidator.
/// </param>
public record RefundPaymentCommand(
    Guid BookingId,
    Guid? DriverId,
    string? Reason = null,
    decimal? Amount = null,
    bool BypassTimeLimit = false) : IRequest<Result>;

// Handlers
public class GetPaymentsHandler(IPaymentRepository repo) : IRequestHandler<GetPaymentsQuery, Result<PagedResult<PaymentDto>>>
{
    public async Task<Result<PagedResult<PaymentDto>>> Handle(GetPaymentsQuery request, CancellationToken ct)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? 10 : request.PageSize > 500 ? 500 : request.PageSize;
        var (items, totalCount) = await repo.GetPagedAsync(page, pageSize, ct);
        var dtos = items.Select(PaymentMapper.ToDto).ToList();
        var pagedResult = new PagedResult<PaymentDto>(dtos, totalCount, page, pageSize);
        return Result.Ok(pagedResult);
    }
}

public class GetPaymentByIdHandler(IPaymentRepository repo) : IRequestHandler<GetPaymentByIdQuery, Result<PaymentDto>>
{
    public async Task<Result<PaymentDto>> Handle(GetPaymentByIdQuery request, CancellationToken ct)
    {
        var payment = await repo.GetByIdAsync(request.Id, ct);
        return payment is null
            ? Result.Fail<PaymentDto>("Payment not found")
            : Result.Ok(PaymentMapper.ToDto(payment));
    }
}

public class GetPaymentsByCustomerHandler(IPaymentRepository repo) : IRequestHandler<GetPaymentsByCustomerQuery, Result<IReadOnlyList<PaymentDto>>>
{
    public async Task<Result<IReadOnlyList<PaymentDto>>> Handle(GetPaymentsByCustomerQuery request, CancellationToken ct)
    {
        var payments = await repo.GetByCustomerIdAsync(request.CustomerId, ct);
        return Result.Ok<IReadOnlyList<PaymentDto>>(payments.Select(PaymentMapper.ToDto).ToList());
    }
}

public class GetPaymentByBookingHandler(IPaymentRepository repo) : IRequestHandler<GetPaymentByBookingQuery, Result<PaymentDto>>
{
    public async Task<Result<PaymentDto>> Handle(GetPaymentByBookingQuery request, CancellationToken ct)
    {
        var payment = await repo.GetByBookingIdAsync(request.BookingId, ct);
        return payment is null
            ? Result.Fail<PaymentDto>("Payment not found")
            : Result.Ok(PaymentMapper.ToDto(payment));
    }
}

public class CreatePaymentHandler(IPaymentRepository repo, IPaymentGatewayFactory gatewayFactory, IPaymentAccessPolicy accessPolicy, ILogger<CreatePaymentHandler> logger) : IRequestHandler<CreatePaymentCommand, Result<PaymentDto>>
{
    public async Task<Result<PaymentDto>> Handle(CreatePaymentCommand request, CancellationToken ct)
    {
        var handlerStartTime = DateTime.UtcNow;
        var dto = request.Dto;

        // Without this, any authenticated caller could create a payment attributed to another
        // customer, which would then surface in that customer's payment list and SSE stream.
        // Checked before the gateway call so a rejected request never creates a real checkout.
        if (!accessPolicy.CanActForCustomer(dto.CustomerId, request.CallerUserId, request.IsElevated))
        {
            logger.LogWarning("[PAYMENT_CREATE] Rejected - caller {CallerUserId} may not create payments for customer {CustomerId}",
                request.CallerUserId, dto.CustomerId);
            return Result.Forbidden<PaymentDto>("You can only create payments for your own account.");
        }

        var method = Enum.Parse<PaymentMethod>(dto.Method, true);

        // New payments always go to the active gateway; the provider is stored on
        // the record so refunds/webhooks keep working after a gateway switch.
        var gateway = gatewayFactory.GetActive();

        logger.LogInformation("[PAYMENT_CREATE] Starting payment creation - CustomerId: {CustomerId}, Amount: {Amount}, BookingId: {BookingId}, Method: {Method}, Gateway: {Gateway}",
            dto.CustomerId, dto.Amount, dto.BookingId?.ToString() ?? "null", dto.Method, gateway.ProviderName);

        // A repeated key returns the original payment rather than opening a second real checkout.
        // The previous dedupe keyed on the provider id returned by the gateway call itself, so it
        // could only catch provider-side dedupe - never a client double-submit, since every call
        // minted a fresh invoice id and PaymentNumber.
        Domain.Payment? payment = null;
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            payment = await repo.GetByCustomerAndIdempotencyKeyAsync(dto.CustomerId, request.IdempotencyKey!.Trim(), ct);

            if (payment is not null && !string.IsNullOrEmpty(payment.ProviderCheckoutUrl))
            {
                logger.LogInformation("[PAYMENT_CREATE] Idempotent replay - returning existing PaymentId: {PaymentId}", payment.Id);
                return Result.Ok(PaymentMapper.ToDto(payment));
            }
            // A row with no checkout URL means a previous attempt persisted but died before or
            // during the gateway call. Fall through and finish it rather than starting over.
        }

        if (payment is null)
        {
            payment = Domain.Payment.Create(dto.BookingId, dto.CustomerId, dto.Amount, method, dto.Currency);
            payment.SetIdempotencyKey(request.IdempotencyKey);

            // Persisted BEFORE the provider call. Previously the checkout was created first, so a
            // crash in between left a payable checkout at the provider that this system had no
            // record of - a customer could pay against a payment we had never heard of.
            repo.Add(payment);
            await repo.SaveChangesAsync(ct);
        }

        var checkoutRequest = new CreateCheckoutRequest(
            ReferenceId: payment.PaymentNumber,
            Amount: dto.Amount,
            PayerEmail: dto.PayerEmail,
            Description: dto.Description,
            Currency: dto.Currency
        );

        var checkoutCreateStartTime = DateTime.UtcNow;
        var session = await gateway.CreateCheckoutAsync(checkoutRequest, ct);

        logger.LogInformation("[PAYMENT_CREATE] Checkout created - Gateway: {Gateway}, ProviderPaymentId: {ProviderPaymentId}, Duration: {Duration}ms",
            gateway.ProviderName, session.ProviderPaymentId, (DateTime.UtcNow - checkoutCreateStartTime).TotalMilliseconds);

        payment.SetProviderCheckout(gateway.ProviderName, session.ProviderPaymentId, session.CheckoutUrl, session.ReferenceId);
        await repo.SaveChangesAsync(ct);

        logger.LogInformation("[PAYMENT_CREATE] Payment saved - PaymentId: {PaymentId}, Gateway: {Gateway}, TotalDuration: {Duration}ms",
            payment.Id, gateway.ProviderName, (DateTime.UtcNow - handlerStartTime).TotalMilliseconds);

        return Result.Ok(PaymentMapper.ToDto(payment));
    }
}

public class ProcessWebhookHandler(IPaymentRepository repo, ILogger<ProcessWebhookHandler> logger) : IRequestHandler<ProcessWebhookCommand, Result>
{
    public async Task<Result> Handle(ProcessWebhookCommand request, CancellationToken ct)
    {
        var payment = await repo.GetByProviderPaymentIdAsync(request.Provider, request.ProviderPaymentId, ct);
        // NotFound is the expected, benign outcome for a driver top-up webhook, which shares this
        // endpoint but has no booking Payment row. Kept distinguishable from a real failure so the
        // controllers can log the two at different severities.
        if (payment is null)
            return Result.NotFound("Payment not found");

        switch (request.Status.ToUpper())
        {
            case "PAID":
            case "SETTLED":
                // Never settle for less (or more) than the payment is for. Mirrors the check the
                // driver top-up path already performs in DriverHandlers.ProcessTopUpWebhook.
                // Returning a failure makes the controller respond 500, so the provider redelivers
                // rather than the mismatch being silently accepted and forgotten.
                if (request.PaidAmount.HasValue && request.PaidAmount.Value != payment.Amount)
                {
                    BeeMetrics.WebhooksFailed.Add(1, new KeyValuePair<string, object?>("reason", "amount_mismatch"));
                    logger.LogError(
                        "[WEBHOOK] Amount mismatch, refusing to settle - Provider: {Provider}, ProviderPaymentId: {ProviderPaymentId}, PaymentId: {PaymentId}, Expected: {Expected}, Received: {Received}",
                        request.Provider, request.ProviderPaymentId, payment.Id, payment.Amount, request.PaidAmount.Value);
                    return Result.Fail($"Payment amount mismatch. Expected: {payment.Amount}, Received: {request.PaidAmount.Value}");
                }

                // Idempotency: avoid duplicate application if the provider sends the webhook more than once
                if (payment.Status != PaymentStatus.Paid)
                    payment.MarkAsPaid(request.PaidAt ?? DateTime.UtcNow);
                break;
            case "EXPIRED":
                payment.MarkAsExpired();
                break;
            case "FAILED":
                payment.MarkAsFailed("Payment failed");
                break;
        }

        await repo.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

public class SetPaymentCaptureIdHandler(IPaymentRepository repo) : IRequestHandler<SetPaymentCaptureIdCommand, Result>
{
    public async Task<Result> Handle(SetPaymentCaptureIdCommand request, CancellationToken ct)
    {
        var payment = await repo.GetByProviderPaymentIdAsync(request.Provider, request.ProviderPaymentId, ct);
        if (payment is null)
            return Result.Fail("Payment not found"); // e.g. driver top-ups have no Payment record

        if (string.IsNullOrEmpty(payment.ProviderCaptureId))
        {
            payment.SetProviderCaptureId(request.CaptureId);
            await repo.SaveChangesAsync(ct);
        }
        return Result.Ok();
    }
}

/// <remarks>
/// Publishes through <see cref="IPaymentOutboxPublisher"/> so <c>PaymentRefundedEvent</c> is
/// staged on the PaymentDbContext outbox and committed in the same transaction as the refund
/// itself (GitLab #65).
///
/// This reverses the earlier rule. The outbox used to exist only on BookingsDbContext, so a
/// scoped IPublishEndpoint here staged onto a context this module never saves and the message was
/// silently dropped — the driver's wallet reversal never ran (GitLab #27). The fix at the time
/// was IBus, which publishes immediately but keeps nothing, so a broker outage lost the event
/// instead. An outbox on this context resolves both: durable *and* delivered.
///
/// This is Payment's own hand-rolled outbox (mirroring the Drivers module's), not MassTransit's
/// built-in EF Core bus outbox: MassTransit 8.x supports only one DbContext per bus for the
/// transactional bus outbox, and this module's outbox coexisting with BookingsDbContext's crashed
/// the host (two BusOutboxDeliveryService loops racing on one shared IBusOutboxNotification).
///
/// The ordering requirement is unchanged: publish **before** the SaveChanges that commits; a
/// message staged after the last save is never flushed.
/// </remarks>
public class RefundPaymentCommandHandler(
    IPaymentRepository repo,
    IPaymentGatewayFactory gatewayFactory,
    IPaymentOutboxPublisher outboxPublisher,
    IMediator mediator,
    IOptions<PaymentRefundOptions> refundOptions,
    ILogger<RefundPaymentCommandHandler> logger) : IRequestHandler<RefundPaymentCommand, Result>
{
    public async Task<Result> Handle(RefundPaymentCommand request, CancellationToken ct)
    {
        var payment = await repo.GetByBookingIdAsync(request.BookingId, ct);
        if (payment == null)
            return Result.Fail("Payment not found");

        if (payment.Status == PaymentStatus.Refunded)
            return Result.Ok(); // Idempotent: already refunded

        if (payment.Status == PaymentStatus.RefundPending)
            return Result.Ok(); // Idempotent: refund already in flight, outcome arrives via refund.* webhook

        if (payment.Status != PaymentStatus.Paid)
            return Result.Fail("Payment is not in Paid status and cannot be refunded");

        if (payment.Method == PaymentMethod.Cash)
            return Result.Fail("Cash payments cannot be refunded through this channel.");

        // Cross-check an admin-supplied driver id against the booking's actual assignment rather
        // than trusting the request body outright - a typo here would otherwise silently debit the
        // wrong driver's wallet reversal. The automatic cancellation path always sources this from
        // the booking's own event data, so it can never disagree with itself here. Fails open when
        // the booking's assignment can't be determined (not found, cross-module read unavailable) -
        // that's not evidence of a mismatch, just missing information; only a *known*, disagreeing
        // assignment blocks the refund.
        if (request.DriverId.HasValue)
        {
            var actualDriverId = await mediator.Send(new GetBookingSelectedDriverIdQuery(request.BookingId), ct);
            if (actualDriverId.HasValue && actualDriverId.Value != request.DriverId.Value)
                return Result.Fail("The supplied driver does not match the driver assigned to this booking.");
        }

        var options = refundOptions.Value;
        if (!request.BypassTimeLimit && options.RefundAllowedWithinHoursAfterPayment > 0 && payment.PaidAt.HasValue)
        {
            var cutoff = payment.PaidAt.Value.AddHours(options.RefundAllowedWithinHoursAfterPayment);
            if (DateTime.UtcNow > cutoff)
                return Result.Fail($"Refund is only allowed within {options.RefundAllowedWithinHoursAfterPayment} hours of payment. Use BypassTimeLimit for exceptions.");
        }

        // Cap against what is still refundable, not the original amount. Capping at payment.Amount
        // held only incidentally - the status guards above short-circuit Refunded and RefundPending,
        // so a second partial refund was blocked by the state machine rather than by this check.
        // Measuring against the cumulative total makes the invariant survive on its own.
        var refundAmount = request.Amount ?? payment.RefundableAmount;
        if (refundAmount <= 0 || refundAmount > payment.RefundableAmount)
            return Result.Fail(
                $"Refund amount must be greater than zero and not exceed the refundable balance of {payment.RefundableAmount:0.00} {payment.Currency} " +
                $"(paid {payment.Amount:0.00}, already refunded {payment.TotalRefunded:0.00}).");

        // Reserve before touching the provider. Flipping status here, guarded by Version/xmin, is
        // the checkpoint that stops two concurrent refund triggers (the admin endpoint and the
        // automatic cancellation consumer, or a plain double-click) from both reaching the gateway:
        // a second Handle() that read this row before this save commits conflicts here instead of
        // racing us to CreateRefundAsync. This matters most for PayMongo, whose refund API has no
        // idempotency-key header of its own to fall back on.
        payment.MarkAsRefundPending(request.DriverId ?? Guid.Empty, refundAmount);
        try
        {
            await repo.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning(
                "[REFUND] Concurrency conflict reserving PaymentId {PaymentId} for refund; another refund attempt is already in flight.",
                payment.Id);
            return Result.Fail("A refund is already being processed for this payment. Please retry shortly.");
        }

        // Switch-safety invariant: refunds always go to the gateway that collected
        // the payment, not the currently active one.
        var gateway = gatewayFactory.Get(payment.Provider);

        var captureId = payment.ProviderCaptureId;
        if (string.IsNullOrEmpty(captureId) && !string.IsNullOrEmpty(payment.ProviderPaymentId))
        {
            var session = await gateway.GetCheckoutAsync(payment.ProviderPaymentId, ct);
            if (session?.ProviderCaptureId != null)
            {
                captureId = session.ProviderCaptureId;
                payment.SetProviderCaptureId(captureId);
                await repo.SaveChangesAsync(ct);
            }
        }

        if (string.IsNullOrEmpty(captureId))
        {
            // Reservation made, no provider call ever attempted - safe to release it.
            await ReleaseReservationAsync(payment, "This payment cannot be refunded through this channel.", ct);
            return Result.Fail("This payment cannot be refunded through this channel.");
        }

        var reason = RefundPaymentCommandValidator.NormalizeReason(request.Reason);
        var createRefund = new CreateGatewayRefundRequest(
            captureId,
            reason,
            refundAmount,
            payment.Currency,
            $"REFUND-{payment.PaymentNumber}");

        GatewayRefund? refundResponse;
        try
        {
            refundResponse = await gateway.CreateRefundAsync(createRefund, ct);
        }
        catch (Exception ex)
        {
            // The provider may or may not have processed this - a thrown exception (timeout,
            // connection reset) means the outcome is genuinely unknown. Reverting to Paid here is
            // exactly what would let a retry double-refund at a provider with no idempotency key
            // (PayMongo). Leave the reservation in place; the stuck-refund reconciliation job
            // settles it later by asking the provider directly, once the outcome is knowable.
            logger.LogError(ex,
                "[REFUND] Gateway threw while creating refund for booking {BookingId}, payment {PaymentId}. Leaving RefundPending for reconciliation.",
                request.BookingId, payment.Id);
            return Result.Fail("Refund request failed and requires reconciliation. Do not retry immediately.");
        }

        if (refundResponse == null)
        {
            // A response was received and the provider said no - unambiguous, safe to release.
            logger.LogWarning("Refund failed for booking {BookingId}, payment {PaymentId}", request.BookingId, payment.Id);
            await ReleaseReservationAsync(payment, "Gateway rejected the refund request.", ct);
            return Result.Fail("Refund could not be completed. Please try again later or contact support.");
        }

        // Provider refunds are asynchronous: acceptance here usually means Pending and
        // the outcome arrives later via a refund webhook (ProcessRefundWebhookHandler).
        // The driver wallet debit (PaymentRefundedEvent) only happens once Succeeded,
        // so a refund that ultimately fails never moves driver money.
        if (refundResponse.Status == GatewayRefundStatus.Succeeded)
        {
            // Pass the amount explicitly. The reservation only records the requested amount, not
            // necessarily what the provider actually moved.
            payment.MarkAsRefunded(DateTime.UtcNow, refundResponse.ProviderRefundId, refundAmount);

            // The provider has already moved the money at this point, so a concurrency conflict
            // here must not lose the outcome - re-read and re-apply rather than failing.
            //
            // The event is staged *before* each save attempt rather than published after a
            // successful one. That used to be the other way round, to stop a failed persist
            // emitting a wallet reversal - but the outbox gives that guarantee properly: staged
            // message and payment row commit in one transaction, so neither can exist without the
            // other. Publishing after the last save would now stage a message nothing flushes.
            //
            // Guid.Empty when no driver was assigned: the consumer looks up a wallet, finds none,
            // and skips - which is the correct outcome for a booking no driver ever worked.
            if (!await SaveWithConcurrencyRetryAsync(
                    payment.Id,
                    p => p.MarkAsRefunded(DateTime.UtcNow, refundResponse.ProviderRefundId, refundAmount),
                    ct,
                    stage: () =>
                    {
                        outboxPublisher.Publish(new PaymentRefundedEvent
                        {
                            BookingId = request.BookingId,
                            DriverId = request.DriverId ?? Guid.Empty,
                            AmountRefunded = refundAmount
                        });
                        return Task.CompletedTask;
                    }))
                return Result.Fail("Refund succeeded at the provider but could not be recorded. Escalate before retrying.");

            BeeMetrics.Refunds.Add(1, new KeyValuePair<string, object?>("outcome", "succeeded"));
        }
        else
        {
            // Already RefundPending from the reservation above; this fills in the real provider
            // id now that it's known.
            payment.MarkAsRefundPending(refundResponse.ProviderRefundId, request.DriverId ?? Guid.Empty, refundAmount);

            if (!await SaveWithConcurrencyRetryAsync(
                    payment.Id,
                    p => p.MarkAsRefundPending(refundResponse.ProviderRefundId, request.DriverId ?? Guid.Empty, refundAmount),
                    ct))
                return Result.Fail("Refund was accepted by the provider but could not be recorded. Escalate before retrying.");

            BeeMetrics.Refunds.Add(1, new KeyValuePair<string, object?>("outcome", "pending"));
        }

        return Result.Ok();
    }

    /// <summary>
    /// Releases a reservation made by the pre-flight <see cref="Domain.Payment.MarkAsRefundPending(Guid, decimal)"/>
    /// call when it's certain no provider refund was ever created, returning the payment to
    /// <see cref="PaymentStatus.Paid"/> so it can be retried. Only call this for outcomes that are
    /// certain - an ambiguous gateway failure must leave the reservation in place instead (see
    /// <see cref="Handle"/>), so the stuck-refund reconciliation job can resolve it against the
    /// provider's own record rather than risk a duplicate refund on retry.
    /// </summary>
    private Task<bool> ReleaseReservationAsync(Domain.Payment payment, string reason, CancellationToken ct)
    {
        payment.MarkRefundFailed(reason);
        return SaveWithConcurrencyRetryAsync(payment.Id, p => p.MarkRefundFailed(reason), ct);
    }

    /// <summary>
    /// Saves, and on an xmin concurrency conflict re-reads the payment and re-applies
    /// <paramref name="reapply"/> once. A conflict means a webhook settled or updated the same row
    /// between our read and our write; the refund outcome still has to be recorded, so losing it
    /// to a stale-row exception is worse than re-applying it.
    /// </summary>
    /// <param name="stage">
    /// Optional outbox publish, invoked immediately before **each** save attempt so the staged
    /// message and the payment row commit together (GitLab #65).
    ///
    /// Re-staging on the retry is deliberate and cannot duplicate: a save that throws commits
    /// nothing, so the first attempt's staged row is never persisted. Staging once up front
    /// instead would be the bug - EF drops the tracked outbox row along with everything else when
    /// the failed save rolls back, and the retry would then commit a refund with no event.
    /// </param>
    private async Task<bool> SaveWithConcurrencyRetryAsync(
        Guid paymentId,
        Action<Domain.Payment> reapply,
        CancellationToken ct,
        Func<Task>? stage = null)
    {
        try
        {
            if (stage != null)
                await stage();

            await repo.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning(
                "[REFUND] Concurrency conflict recording refund outcome for PaymentId {PaymentId}; re-reading and re-applying.",
                paymentId);
        }

        var fresh = await repo.GetByIdAsync(paymentId, ct);
        if (fresh is null)
            return false;

        reapply(fresh);

        try
        {
            if (stage != null)
                await stage();

            await repo.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            logger.LogError(ex,
                "[REFUND] Could not record refund outcome for PaymentId {PaymentId} after retry.",
                paymentId);
            return false;
        }
    }
}

/// <summary>
/// Finalizes an asynchronous Xendit refund when the refund.* webhook arrives.
/// On success the payment becomes Refunded and PaymentRefundedEvent is published
/// (driver wallet debit). On failure the payment returns to Paid.
/// </summary>
/// <remarks>
/// Uses <see cref="IPaymentOutboxPublisher"/> for the same reason as
/// <see cref="RefundPaymentCommandHandler"/>: the outbox lives on PaymentDbContext, which
/// this path does save, so the event commits transactionally with the refund (GitLab #65).
/// Publish before the save, not after.
/// </remarks>
public class ProcessRefundWebhookHandler(
    IPaymentRepository repo,
    IPaymentOutboxPublisher outboxPublisher,
    ILogger<ProcessRefundWebhookHandler> logger) : IRequestHandler<ProcessRefundWebhookCommand, Result>
{
    public async Task<Result> Handle(ProcessRefundWebhookCommand request, CancellationToken ct)
    {
        var payment = await repo.GetByProviderRefundIdAsync(request.Provider, request.ProviderRefundId, ct);
        if (payment is null)
            return Result.Fail($"No payment found for refund {request.ProviderRefundId}");

        switch (request.Status.ToUpperInvariant())
        {
            case "SUCCEEDED":
                if (payment.Status == PaymentStatus.Refunded)
                    return Result.Ok(); // Idempotent: webhook redelivered

                // Capture the driver before marking: the reversal has to reverse the amount this
                // particular refund moved, and reading it back off the entity afterwards would be
                // one refactor away from picking up a cumulative total instead.
                var bookingId = payment.BookingId;
                var refundDriverId = payment.RefundDriverId;
                var amountRefunded = payment.RefundAmount ?? payment.Amount;

                payment.MarkAsRefunded(DateTime.UtcNow, request.ProviderRefundId, amountRefunded);

                // Staged before the save so the event and the Refunded status commit together.
                // Previously published after it, which with an outbox would stage a message that
                // nothing ever flushes - the wallet reversal would silently never run.
                if (bookingId.HasValue && refundDriverId.HasValue)
                {
                    outboxPublisher.Publish(new PaymentRefundedEvent
                    {
                        BookingId = bookingId.Value,
                        DriverId = refundDriverId.Value,
                        AmountRefunded = amountRefunded
                    });
                }
                else
                {
                    logger.LogWarning(
                        "Refund {RefundId} succeeded but payment {PaymentId} has no booking/driver context; wallet reversal skipped",
                        request.ProviderRefundId, payment.Id);
                }

                await repo.SaveChangesAsync(ct);
                BeeMetrics.Refunds.Add(1, new KeyValuePair<string, object?>("outcome", "succeeded"));
                break;

            case "FAILED":
                payment.MarkRefundFailed(request.FailureReason ?? "Refund failed at provider");
                await repo.SaveChangesAsync(ct);
                logger.LogWarning(
                    "Refund {RefundId} for payment {PaymentId} failed at Xendit: {Reason}. Payment returned to Paid.",
                    request.ProviderRefundId, payment.Id, request.FailureReason ?? "unknown");
                BeeMetrics.Refunds.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
                break;

            default:
                logger.LogInformation(
                    "Ignoring refund webhook status {Status} for refund {RefundId}", request.Status, request.ProviderRefundId);
                break;
        }

        return Result.Ok();
    }
}

public class LinkPaymentToBookingHandler(IPaymentRepository repo, IPaymentAccessPolicy accessPolicy) : IRequestHandler<LinkPaymentToBookingCommand, Result>
{
    public async Task<Result> Handle(LinkPaymentToBookingCommand request, CancellationToken ct)
    {
        var payment = await repo.GetByIdAsync(request.PaymentId, ct);
        if (payment == null)
            return Result.NotFound("Payment not found");

        // Both sides must belong to the caller. Guarding only one of them still allows a
        // caller to settle their own booking with someone else's payment, or someone else's
        // booking with their own - and EarningCreditConsumer derives the driver's fare from
        // whichever payment ends up linked.
        //
        // Same message and shape for every denial (including a booking that does not exist)
        // so this endpoint cannot be used to probe which payment or booking ids are real.
        if (!accessPolicy.CanActForCustomer(payment.CustomerId, request.CallerUserId, request.IsElevated) ||
            !await accessPolicy.CanLinkToBookingAsync(request.BookingId, request.CallerUserId, request.IsElevated, ct))
            return Result.NotFound("Payment not found");

        payment.LinkToBooking(request.BookingId);
        await repo.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

internal static class PaymentMapper
{
    public static PaymentDto ToDto(Domain.Payment p) => new(
        p.Id,
        p.PaymentNumber,
        p.BookingId,
        p.CustomerId,
        p.Amount,
        p.Currency,
        p.Status.ToString(),
        p.Method.ToString(),
        p.ProviderCheckoutUrl,
        p.PaidAt,
        p.CreatedAt
    );
}
