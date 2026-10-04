using BeeLogistics.Modules.Notification.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Notification.Application.Services;

/// <summary>
/// Service for sending booking-related emails.
/// This service wraps the email template generation and sending logic.
/// </summary>
public interface IBookingEmailService
{
    Task SendBookingConfirmationAsync(
        string customerEmail,
        string customerName,
        string bookingNumber,
        string pickupLocation,
        string dropoffLocation,
        DateTime scheduleDate,
        string truckType,
        string cargoDescription,
        CancellationToken ct = default);

    Task SendBookingDispatchedAsync(
        string customerEmail,
        string customerName,
        string bookingNumber,
        string pickupLocation,
        string dropoffLocation,
        DateTime scheduleDate,
        CancellationToken ct = default);

    Task SendBookingInProgressAsync(
        string customerEmail,
        string customerName,
        string bookingNumber,
        string dropoffLocation,
        CancellationToken ct = default);

    Task SendBookingDeliveredAsync(
        string customerEmail,
        string customerName,
        string bookingNumber,
        string dropoffLocation,
        DateTime deliveredAt,
        CancellationToken ct = default);

    Task SendBookingCancelledAsync(
        string customerEmail,
        string customerName,
        string bookingNumber,
        string pickupLocation,
        string dropoffLocation,
        DateTime cancelledAt,
        CancellationToken ct = default);

    Task SendPaymentReceiptAsync(
        string customerEmail,
        string customerName,
        string paymentNumber,
        decimal amount,
        string currency,
        string? invoiceUrl,
        DateTime paidAt,
        CancellationToken ct = default);
}

public class BookingEmailService : IBookingEmailService
{
    private readonly IEmailService _emailService;
    private readonly IEmailTemplateService _emailTemplateService;
    private readonly ILogger<BookingEmailService> _logger;

    public BookingEmailService(
        IEmailService emailService, 
        IEmailTemplateService emailTemplateService,
        ILogger<BookingEmailService> logger)
    {
        _emailService = emailService;
        _emailTemplateService = emailTemplateService;
        _logger = logger;
    }

    public async Task SendBookingConfirmationAsync(
        string customerEmail,
        string customerName,
        string bookingNumber,
        string pickupLocation,
        string dropoffLocation,
        DateTime scheduleDate,
        string truckType,
        string cargoDescription,
        CancellationToken ct = default)
    {
        try
        {
            var email = _emailTemplateService.CreateBookingConfirmationEmail(
                customerEmail,
                customerName,
                bookingNumber,
                pickupLocation,
                dropoffLocation,
                scheduleDate,
                truckType,
                cargoDescription);

            await _emailService.SendAsync(email, ct);
            _logger.LogInformation("Booking confirmation email queued for {BookingNumber} to {Email}", 
                bookingNumber, customerEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send booking confirmation email for {BookingNumber} to {Email}", 
                bookingNumber, customerEmail);
            // Don't throw - email failure shouldn't fail the booking
        }
    }

    public async Task SendBookingDispatchedAsync(
        string customerEmail,
        string customerName,
        string bookingNumber,
        string pickupLocation,
        string dropoffLocation,
        DateTime scheduleDate,
        CancellationToken ct = default)
    {
        try
        {
            var email = _emailTemplateService.CreateBookingDispatchedEmail(
                customerEmail,
                customerName,
                bookingNumber,
                pickupLocation,
                dropoffLocation,
                scheduleDate);

            await _emailService.SendAsync(email, ct);
            _logger.LogInformation("Booking dispatched email queued for {BookingNumber} to {Email}", 
                bookingNumber, customerEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send booking dispatched email for {BookingNumber} to {Email}", 
                bookingNumber, customerEmail);
        }
    }

    public async Task SendBookingInProgressAsync(
        string customerEmail,
        string customerName,
        string bookingNumber,
        string dropoffLocation,
        CancellationToken ct = default)
    {
        try
        {
            var email = _emailTemplateService.CreateBookingInProgressEmail(
                customerEmail,
                customerName,
                bookingNumber,
                dropoffLocation);

            await _emailService.SendAsync(email, ct);
            _logger.LogInformation("Booking in-progress email queued for {BookingNumber} to {Email}", 
                bookingNumber, customerEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send booking in-progress email for {BookingNumber} to {Email}", 
                bookingNumber, customerEmail);
        }
    }

    public async Task SendBookingDeliveredAsync(
        string customerEmail,
        string customerName,
        string bookingNumber,
        string dropoffLocation,
        DateTime deliveredAt,
        CancellationToken ct = default)
    {
        try
        {
            var email = _emailTemplateService.CreateBookingDeliveredEmail(
                customerEmail,
                customerName,
                bookingNumber,
                dropoffLocation,
                deliveredAt);

            await _emailService.SendAsync(email, ct);
            _logger.LogInformation("Booking delivered email queued for {BookingNumber} to {Email}", 
                bookingNumber, customerEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send booking delivered email for {BookingNumber} to {Email}", 
                bookingNumber, customerEmail);
        }
    }

    public async Task SendBookingCancelledAsync(
        string customerEmail,
        string customerName,
        string bookingNumber,
        string pickupLocation,
        string dropoffLocation,
        DateTime cancelledAt,
        CancellationToken ct = default)
    {
        try
        {
            var email = _emailTemplateService.CreateBookingCancelledEmail(
                customerEmail,
                customerName,
                bookingNumber,
                pickupLocation,
                dropoffLocation,
                cancelledAt);

            await _emailService.SendAsync(email, ct);
            _logger.LogInformation("Booking cancelled email queued for {BookingNumber} to {Email}", 
                bookingNumber, customerEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send booking cancelled email for {BookingNumber} to {Email}", 
                bookingNumber, customerEmail);
        }
    }

    public async Task SendPaymentReceiptAsync(
        string customerEmail,
        string customerName,
        string paymentNumber,
        decimal amount,
        string currency,
        string? invoiceUrl,
        DateTime paidAt,
        CancellationToken ct = default)
    {
        try
        {
            var email = _emailTemplateService.CreatePaymentReceiptEmail(
                customerEmail,
                customerName,
                paymentNumber,
                amount,
                currency,
                invoiceUrl,
                paidAt);

            await _emailService.SendAsync(email, ct);
            _logger.LogInformation("Payment receipt email queued for {PaymentNumber} to {Email}", paymentNumber, customerEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send payment receipt email for {PaymentNumber} to {Email}", paymentNumber, customerEmail);
        }
    }
}
