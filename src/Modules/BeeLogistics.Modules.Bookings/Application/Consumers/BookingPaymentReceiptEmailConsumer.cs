using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Notification.Application.Services;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Bookings.Application.Consumers;

/// <summary>
/// Sends a payment receipt email to the customer when a booking (delivery) payment is marked paid.
/// </summary>
public class BookingPaymentReceiptEmailConsumer : IConsumer<BookingPaymentReceiptRequestedEvent>
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IBookingEmailService _bookingEmailService;
    private readonly ILogger<BookingPaymentReceiptEmailConsumer> _logger;

    public BookingPaymentReceiptEmailConsumer(
        UserManager<ApplicationUser> userManager,
        IBookingEmailService bookingEmailService,
        ILogger<BookingPaymentReceiptEmailConsumer> logger)
    {
        _userManager = userManager;
        _bookingEmailService = bookingEmailService;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<BookingPaymentReceiptRequestedEvent> context)
    {
        var msg = context.Message;

        var user = await _userManager.FindByIdAsync(msg.CustomerId.ToString());
        if (user == null || string.IsNullOrWhiteSpace(user.Email))
        {
            _logger.LogWarning("BookingPaymentReceiptEmailConsumer: customer {CustomerId} not found or has no email, skipping receipt for payment {PaymentNumber}",
                msg.CustomerId, msg.PaymentNumber);
            return;
        }

        try
        {
            var customerName = string.IsNullOrWhiteSpace(user.FullName) ? user.Email.Split('@')[0] : user.FullName;
            await _bookingEmailService.SendPaymentReceiptAsync(
                user.Email,
                customerName,
                msg.PaymentNumber,
                msg.Amount,
                msg.Currency,
                msg.CheckoutUrl,
                msg.PaidAt,
                context.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send payment receipt email for payment {PaymentNumber} to customer {CustomerId}",
                msg.PaymentNumber, msg.CustomerId);
            throw;
        }
    }
}
