using BeeLogistics.Modules.Payment.Application.DTOs;
using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.DTOs;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace BeeLogistics.Modules.Payment.Presentation.Controllers;

public class PaymentsController : BaseController
{
    private readonly IMediator _mediator;
    private readonly IAuthorizationService _authorizationService;
    private readonly IBookingPaymentEventBroadcaster? _eventBroadcaster;
    private readonly ILogger<PaymentsController>? _logger;

    public PaymentsController(IMediator mediator, IAuthorizationService authorizationService, IBookingPaymentEventBroadcaster? eventBroadcaster = null, ILogger<PaymentsController>? logger = null)
    {
        _mediator = mediator;
        _authorizationService = authorizationService;
        _eventBroadcaster = eventBroadcaster;
        _logger = logger;
    }

    /// <summary>
    /// The caller's ASP.NET Identity user id, or null when the token carries no usable subject.
    /// </summary>
    private Guid? GetCallerUserId()
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    /// <summary>
    /// Whether the caller satisfies the "Backoffice" policy, evaluated through
    /// IAuthorizationService so the policy stays defined in exactly one place (Program.cs).
    /// This is deliberately broader than the role check in <see cref="CanAccessCustomer"/>,
    /// which the read endpoints keep unchanged — the writes below are new surface, so they
    /// adopt the same policy the Bookings module already uses for object-level checks.
    /// </summary>
    private async Task<bool> IsElevatedAsync()
        => (await _authorizationService.AuthorizeAsync(User, "Backoffice")).Succeeded;

    // Listing every payment is a back-office operation; regular users must only see their own.
    [HttpGet]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public async Task<IActionResult> GetAll(CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        => FromResult(await _mediator.Send(new GetPaymentsQuery(page, pageSize), ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var result = await _mediator.Send(new GetPaymentByIdQuery(id), ct);
        // Don't let one authenticated user read another user's payment record.
        // Return NotFound (not 403) so the endpoint doesn't confirm the id exists.
        if (result.IsSuccess && result.Value is not null && !CanAccessCustomer(result.Value.CustomerId))
            return NotFound(ApiResponse<PaymentDto>.Fail("Payment not found"));
        return FromResult(result);
    }

    [HttpGet("booking/{bookingId:guid}")]
    public async Task<IActionResult> GetByBooking(Guid bookingId, CancellationToken ct)
    {
        var result = await _mediator.Send(new GetPaymentByBookingQuery(bookingId), ct);
        if (result.IsSuccess && result.Value is not null && !CanAccessCustomer(result.Value.CustomerId))
            return NotFound(ApiResponse<PaymentDto>.Fail("Payment not found"));
        return FromResult(result);
    }

    [HttpGet("customer/{customerId:guid}")]
    public async Task<IActionResult> GetByCustomer(Guid customerId, CancellationToken ct)
    {
        if (!CanAccessCustomer(customerId))
            return StatusCode(StatusCodes.Status403Forbidden,
                ApiResponse<IReadOnlyList<PaymentDto>>.Fail("Forbidden: You can only access your own payments"));
        return FromResult(await _mediator.Send(new GetPaymentsByCustomerQuery(customerId), ct));
    }

    /// <summary>
    /// True if the caller may read payments belonging to <paramref name="customerId"/>:
    /// either they are that customer (Payment.CustomerId is the Identity UserId — see the
    /// SSE events endpoint, which compares the same claim) or they are back-office staff.
    /// </summary>
    private bool CanAccessCustomer(Guid customerId)
    {
        if (User.IsInRole("SuperAdmin") || User.IsInRole("Admin"))
            return true;
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(userIdClaim, out var currentUserId) && currentUserId == customerId;
    }

    /// <param name="idempotencyKey">
    /// Optional. Repeat the same key to retry safely: the original payment is returned instead of
    /// a second checkout being opened at the provider. Omitting it preserves the previous
    /// behaviour, so existing clients are unaffected.
    /// </param>
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreatePaymentDto dto,
        CancellationToken ct,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey = null)
    {
        var callerUserId = GetCallerUserId();
        if (callerUserId is null)
            return Unauthorized(ApiResponse<PaymentDto>.Fail("Invalid token"));

        return FromResult(await _mediator.Send(
            new CreatePaymentCommand(dto, callerUserId.Value, await IsElevatedAsync(), idempotencyKey), ct));
    }

    /// <summary>
    /// Initiate refund for a cashless payment (back office only). Time-limited unless BypassTimeLimit is true.
    /// </summary>
    /// <summary>
    /// Link a payment to a booking (for PayOnline flow where payment is created before booking).
    /// </summary>
    [HttpPost("{paymentId:guid}/link-booking/{bookingId:guid}")]
    public async Task<IActionResult> LinkPaymentToBooking(Guid paymentId, Guid bookingId, CancellationToken ct)
    {
        var callerUserId = GetCallerUserId();
        if (callerUserId is null)
            return Unauthorized(ApiResponse.Fail("Invalid token"));

        return FromResult(await _mediator.Send(
            new LinkPaymentToBookingCommand(paymentId, bookingId, callerUserId.Value, await IsElevatedAsync()), ct));
    }

    [HttpPost("refund")]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public async Task<IActionResult> Refund([FromBody] RefundPaymentRequest request, CancellationToken ct)
        => FromResult(await _mediator.Send(new RefundPaymentCommand(
            request.BookingId,
            request.DriverId,
            request.Reason,
            request.Amount,
            request.BypassTimeLimit), ct));

    /// <summary>
    /// SSE stream for real-time booking payment paid events. When a webhook marks a payment as paid, this stream receives an event so the app can update immediately.
    /// Optionally accepts paymentId query parameter to check payment status immediately on connect (handles case where payment was already confirmed before SSE connected).
    /// </summary>
    [HttpGet("customer/{customerId:guid}/events")]
    [Produces("text/event-stream")]
    public async Task GetPaymentEvents(Guid customerId, [FromQuery] Guid? paymentId, CancellationToken ct = default)
    {
        var endpointStartTime = DateTime.UtcNow;
        _logger?.LogInformation("[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] ===== SSE ENDPOINT CALLED ===== CustomerId: {CustomerId}, PaymentId: {PaymentId}", 
            endpointStartTime, customerId, paymentId?.ToString() ?? "null");
        
        // Ensure customer can only access their own payment events
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var currentUserId) || currentUserId != customerId)
        {
            _logger?.LogWarning("[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] SSE endpoint access denied - UserIdClaim: {UserIdClaim}, CustomerId: {CustomerId}", 
                DateTime.UtcNow, userIdClaim ?? "null", customerId);
            Response.StatusCode = 403;
            var forbidden = Encoding.UTF8.GetBytes("Forbidden: You can only access your own payment events");
            await Response.Body.WriteAsync(forbidden, ct);
            return;
        }

        if (_eventBroadcaster == null)
        {
            _logger?.LogError("[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] SSE endpoint error - Event broadcaster is null", 
                DateTime.UtcNow);
            Response.StatusCode = 501;
            var notConfigured = Encoding.UTF8.GetBytes("Payment events not configured");
            await Response.Body.WriteAsync(notConfigured, ct);
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        // Send initial comment to establish SSE connection immediately
        // This ensures the client's fetch call resolves and connection is established before any async operations
        try
        {
            var initialComment = Encoding.UTF8.GetBytes(": connected\n\n");
            await Response.Body.WriteAsync(initialComment, ct);
            await Response.Body.FlushAsync(ct);
            _logger?.LogInformation("[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] SSE connection established - Initial comment sent - CustomerId: {CustomerId}, PaymentId: {PaymentId}", 
                DateTime.UtcNow, customerId, paymentId?.ToString() ?? "null");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Failed to send initial SSE comment - CustomerId: {CustomerId}", 
                DateTime.UtcNow, customerId);
            throw;
        }

        // If paymentId is provided, check if payment is already paid and send status immediately
        // This handles the case where payment was confirmed before SSE connection was established
        // Similar to driver topup pattern - check status on connect to avoid race conditions
        if (paymentId.HasValue)
        {
            var statusCheckStartTime = DateTime.UtcNow;
            _logger?.LogInformation("[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Checking payment status on SSE connect - PaymentId: {PaymentId}, CustomerId: {CustomerId}", 
                statusCheckStartTime, paymentId.Value, customerId);
            
            var paymentResult = await _mediator.Send(new GetPaymentByIdQuery(paymentId.Value), ct);
            var statusCheckEndTime = DateTime.UtcNow;
            
            if (paymentResult.IsSuccess && paymentResult.Value != null)
            {
                var payment = paymentResult.Value;
                _logger?.LogInformation("[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Payment found - PaymentId: {PaymentId}, Status: {Status}, CustomerId: {CustomerId}, BookingId: {BookingId}, Duration: {Duration}ms", 
                    statusCheckEndTime, payment.Id, payment.Status, payment.CustomerId, payment.BookingId?.ToString() ?? "null", (statusCheckEndTime - statusCheckStartTime).TotalMilliseconds);
                
                // Only send if payment belongs to this customer and is already paid
                if (payment.CustomerId == customerId && payment.Status == "Paid")
                {
                    var bookingIdValue = payment.BookingId; // Already Guid? from PaymentDto
                    var json = JsonSerializer.Serialize(new
                    {
                        PaymentId = payment.Id,
                        BookingId = bookingIdValue,
                        CustomerId = payment.CustomerId,
                        Amount = payment.Amount,
                        Status = "Paid",
                        PaidAtUtc = payment.PaidAt?.ToString("O") ?? DateTime.UtcNow.ToString("O")
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    var chunk = Encoding.UTF8.GetBytes("data: " + json + "\n\n");
                    await Response.Body.WriteAsync(chunk, ct);
                    await Response.Body.FlushAsync(ct);
                    
                    _logger?.LogInformation("[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Payment already paid - Sent status immediately via SSE - PaymentId: {PaymentId}, BookingId: {BookingId}", 
                        DateTime.UtcNow, payment.Id, bookingIdValue?.ToString() ?? "null");
                }
                else if (payment.CustomerId == customerId)
                {
                    _logger?.LogInformation("[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Payment not paid yet - Status: {Status}, Waiting for webhook event - PaymentId: {PaymentId}", 
                        DateTime.UtcNow, payment.Status, payment.Id);
                }
                else
                {
                    _logger?.LogWarning("[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Payment belongs to different customer - PaymentId: {PaymentId}, PaymentCustomerId: {PaymentCustomerId}, RequestCustomerId: {RequestCustomerId}", 
                        DateTime.UtcNow, payment.Id, payment.CustomerId, customerId);
                }
            }
            else
            {
                _logger?.LogWarning("[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Payment not found - PaymentId: {PaymentId}, Error: {Error}", 
                    statusCheckEndTime, paymentId.Value, paymentResult.IsSuccess ? "null" : paymentResult.Error);
            }
        }
        else
        {
            _logger?.LogInformation("[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] SSE connected without paymentId - CustomerId: {CustomerId}, Will only receive future events", 
                DateTime.UtcNow, customerId);
        }

        try
        {
            var sseConnectTime = DateTime.UtcNow;
            _logger?.LogInformation("[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] SSE stream started - CustomerId: {CustomerId}, PaymentId: {PaymentId}", 
                sseConnectTime, customerId, paymentId?.ToString() ?? "null");
            
            // Send keep-alive comments every 30 seconds to prevent proxy/load balancer timeouts (504 Gateway Timeout)
            // SSE spec: Comments (lines starting with ':') are ignored by clients but keep connection alive
            var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var keepAliveTask = Task.Run(async () =>
            {
                try
                {
                    while (!keepAliveCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), keepAliveCts.Token);
                        if (!keepAliveCts.Token.IsCancellationRequested)
                        {
                            var keepAliveComment = Encoding.UTF8.GetBytes(": keep-alive\n\n");
                            await Response.Body.WriteAsync(keepAliveComment, keepAliveCts.Token);
                            await Response.Body.FlushAsync(keepAliveCts.Token);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when connection closes
                }
                catch (Exception ex)
                {
                            _logger?.LogWarning(ex, "[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Keep-alive error - CustomerId: {CustomerId}", 
                                DateTime.UtcNow, customerId);
                }
            }, keepAliveCts.Token);
            
            try
            {
                await foreach (var payload in _eventBroadcaster.SubscribeAsync(customerId, ct))
                {
                    var eventSendTime = DateTime.UtcNow;
                    // Convert Empty Guid to null for JSON (cleaner for frontend)
                    var bookingIdValue = payload.BookingId == Guid.Empty ? (Guid?)null : payload.BookingId;
                    var json = JsonSerializer.Serialize(new
                    {
                        payload.PaymentId,
                        BookingId = bookingIdValue,
                        payload.CustomerId,
                        payload.Amount,
                        payload.Status,
                        payload.PaidAtUtc
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    var chunk = Encoding.UTF8.GetBytes("data: " + json + "\n\n");
                    await Response.Body.WriteAsync(chunk, ct);
                    await Response.Body.FlushAsync(ct);
                    
                    _logger?.LogInformation("[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Payment event sent via SSE - PaymentId: {PaymentId}, CustomerId: {CustomerId}, Status: {Status}", 
                        eventSendTime, payload.PaymentId, payload.CustomerId, payload.Status);
                }
            }
            finally
            {
                // Stop keep-alive when stream ends
                keepAliveCts.Cancel();
                try
                {
                    await keepAliveTask;
                }
                catch
                {
                    // Ignore cancellation exceptions
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
            _logger?.LogInformation("[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] SSE stream cancelled - CustomerId: {CustomerId}", 
                DateTime.UtcNow, customerId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[XENDIT] [SSE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] SSE stream error - CustomerId: {CustomerId}", 
                DateTime.UtcNow, customerId);
            throw;
        }
    }
}
