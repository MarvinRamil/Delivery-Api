using BeeLogistics.Modules.Notification.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using System.Text;

namespace BeeLogistics.Modules.Notification.Application.Services;

/// <summary>
/// Service for generating HTML email templates
/// </summary>
public interface IEmailTemplateService
{
    EmailMessage CreateVerificationEmail(string to, string fullName, string verificationToken);
    EmailMessage CreatePasswordResetEmail(string to, string fullName, string resetToken);
    EmailMessage CreatePasswordChangedEmail(string to, string fullName, DateTime changedAt);
    EmailMessage CreateWelcomeEmail(string to, string fullName);
    EmailMessage CreateBookingConfirmationEmail(
        string to,
        string customerName,
        string bookingNumber,
        string pickupLocation,
        string dropoffLocation,
        DateTime scheduleDate,
        string truckType,
        string cargoDescription);
    EmailMessage CreateBookingDispatchedEmail(
        string to,
        string customerName,
        string bookingNumber,
        string pickupLocation,
        string dropoffLocation,
        DateTime scheduleDate);
    EmailMessage CreateBookingInProgressEmail(
        string to,
        string customerName,
        string bookingNumber,
        string dropoffLocation);
    EmailMessage CreateBookingDeliveredEmail(
        string to,
        string customerName,
        string bookingNumber,
        string dropoffLocation,
        DateTime deliveredAt);
    EmailMessage CreateBookingCancelledEmail(
        string to,
        string customerName,
        string bookingNumber,
        string pickupLocation,
        string dropoffLocation,
        DateTime cancelledAt);
    EmailMessage CreateWithdrawalReceiptEmail(
        string to,
        string driverName,
        decimal amount,
        DateTime requestedAt,
        string maskedAccountNumber,
        string bankName,
        string? xenditPayoutId = null,
        string? xenditPayoutUrl = null);
    EmailMessage CreatePaymentReceiptEmail(
        string to,
        string customerName,
        string paymentNumber,
        decimal amount,
        string currency,
        string? invoiceUrl,
        DateTime paidAt);
    EmailMessage CreateDriverApplicationApprovedEmail(string to, string fullName);
    EmailMessage CreateDriverApplicationRejectedEmail(string to, string fullName, string? notes);

    /// <summary>
    /// Wrap an arbitrary subject + message (used by the backoffice "Send Notification"
    /// composer) in the standard branded email layout (logo header, footer, styling).
    /// </summary>
    EmailMessage CreateCustomNotificationEmail(string to, string subject, string messageHtml);
}

public class EmailTemplateService : IEmailTemplateService
{
    private readonly string _baseUrl;
    private const string AppName = "My Bee App On-Demand";
    private static string? _logoBase64;
    private static readonly Dictionary<string, string> _templateCache = new();

    public EmailTemplateService(IConfiguration configuration)
    {
        // Get base URL from configuration, fallback to production URL if not set
        // Priority: Email:BaseUrl > FRONTEND_URL > default production URL
        _baseUrl = configuration["Email:BaseUrl"] 
            ?? configuration["FRONTEND_URL"] 
            ?? "https://mybeeapp.com";
    }

    /// <summary>
    /// Load template from embedded resource
    /// </summary>
    private static string LoadTemplate(string templateName)
    {
        if (_templateCache.TryGetValue(templateName, out var cached))
            return cached;

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"BeeLogistics.Modules.Notification.Templates.Email.{templateName}";
            
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new FileNotFoundException($"Template '{templateName}' not found as embedded resource '{resourceName}'");
            }

            using var reader = new StreamReader(stream);
            var template = reader.ReadToEnd();
            _templateCache[templateName] = template;
            return template;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load email template '{templateName}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Replace placeholders in template
    /// </summary>
    private static string ReplacePlaceholders(string template, Dictionary<string, string> placeholders)
    {
        var result = template;
        foreach (var (key, value) in placeholders)
        {
            result = result.Replace($"{{{key}}}", value);
        }
        return result;
    }

    /// <summary>
    /// Get logo as base64 encoded string for email embedding
    /// </summary>
    private static string GetLogoBase64()
    {
        if (_logoBase64 != null)
            return _logoBase64;

        try
        {
            // Try to find logo in Identity module assets
            var possiblePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Modules", "BeeLogistics.Modules.Identity", "assets", "bee_logo.png"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "bee_logo.png"),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "Modules", "BeeLogistics.Modules.Identity", "assets", "bee_logo.png"),
                Path.Combine(Directory.GetCurrentDirectory(), "assets", "bee_logo.png")
            };

            foreach (var path in possiblePaths)
            {
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    var imageBytes = File.ReadAllBytes(fullPath);
                    _logoBase64 = Convert.ToBase64String(imageBytes);
                    return _logoBase64;
                }
            }
        }
        catch
        {
            // If logo can't be loaded, return empty string (will use text fallback)
        }

        return string.Empty;
    }

    /// <summary>
    /// Get email header HTML with logo
    /// </summary>
    private static string GetEmailHeader()
    {
        var logoBase64 = GetLogoBase64();
        var logoImg = !string.IsNullOrEmpty(logoBase64)
            ? $@"<img src=""data:image/png;base64,{logoBase64}"" alt=""MyBeeApp Logo"" style=""max-width: 180px; height: auto; display: block; margin: 0 auto;"" />"
            : @"<h1 style=""margin: 0; color: #1c190d; font-size: 32px; font-weight: 700; letter-spacing: -0.5px;"">MyBeeApp</h1>";

        return $@"
        <tr>
            <td style=""padding: 40px 20px; background: linear-gradient(135deg, #FFD700 0%, #FFC700 100%); text-align: center;"">
                {logoImg}
            </td>
        </tr>";
    }

    /// <summary>
    /// Wrap an arbitrary subject + message in the standard branded layout.
    /// The subject is used as both the email subject and the in-body heading.
    /// </summary>
    public EmailMessage CreateCustomNotificationEmail(string to, string subject, string messageHtml)
    {
        var template = LoadTemplate("custom-notification-email.html");
        var placeholders = new Dictionary<string, string>
        {
            { "AppName", AppName },
            { "Title", subject },
            { "Year", DateTime.UtcNow.Year.ToString() },
            { "Header", GetEmailHeader() },
            // Replace {Message} last so branded chrome is applied before user content is injected.
            { "Message", messageHtml },
        };
        var htmlBody = ReplacePlaceholders(template, placeholders);
        return new EmailMessage(to, subject, htmlBody, IsHtml: true);
    }

    /// <summary>
    /// Generate email verification email HTML
    /// </summary>
    public EmailMessage CreateVerificationEmail(string to, string fullName, string verificationToken)
    {
        var verificationUrl = $"{_baseUrl}/verify-email?token={Uri.EscapeDataString(verificationToken)}&email={Uri.EscapeDataString(to)}";
        
        var template = LoadTemplate("verification-email.html");
        var placeholders = new Dictionary<string, string>
        {
            { "AppName", AppName },
            { "FullName", fullName },
            { "VerificationUrl", verificationUrl },
            { "Year", DateTime.UtcNow.Year.ToString() },
            { "Header", GetEmailHeader() }
        };

        var htmlBody = ReplacePlaceholders(template, placeholders);

        return new EmailMessage(
            To: to,
            Subject: $"Verify Your Email Address - {AppName}",
            Body: htmlBody,
            IsHtml: true
        );
    }

    /// <summary>
    /// Generate password reset email HTML
    /// </summary>
    public EmailMessage CreatePasswordResetEmail(string to, string fullName, string resetToken)
    {
        var resetUrl = $"{_baseUrl}/reset-password?token={Uri.EscapeDataString(resetToken)}&email={Uri.EscapeDataString(to)}";
        
        var template = LoadTemplate("password-reset-email.html");
        var placeholders = new Dictionary<string, string>
        {
            { "AppName", AppName },
            { "FullName", fullName },
            { "ResetUrl", resetUrl },
            { "Year", DateTime.UtcNow.Year.ToString() },
            { "Header", GetEmailHeader() }
        };

        var htmlBody = ReplacePlaceholders(template, placeholders);

        return new EmailMessage(
            To: to,
            Subject: $"Reset Your Password - {AppName}",
            Body: htmlBody,
            IsHtml: true
        );
    }

    /// <summary>
    /// Generate password changed notification email HTML
    /// </summary>
    public EmailMessage CreatePasswordChangedEmail(string to, string fullName, DateTime changedAt)
    {
        var template = LoadTemplate("password-changed-email.html");
        var placeholders = new Dictionary<string, string>
        {
            { "AppName", AppName },
            { "FullName", fullName },
            { "ChangedAt", changedAt.ToString("MMMM dd, yyyy 'at' HH:mm") + " UTC" },
            { "Year", DateTime.UtcNow.Year.ToString() },
            { "Header", GetEmailHeader() }
        };

        var htmlBody = ReplacePlaceholders(template, placeholders);

        return new EmailMessage(
            To: to,
            Subject: $"Password Changed - {AppName}",
            Body: htmlBody,
            IsHtml: true
        );
    }

    /// <summary>
    /// Generate welcome email HTML (after email verification)
    /// </summary>
    public EmailMessage CreateWelcomeEmail(string to, string fullName)
    {
        var template = LoadTemplate("welcome-email.html");
        var placeholders = new Dictionary<string, string>
        {
            { "AppName", AppName },
            { "FullName", fullName },
            { "BaseUrl", _baseUrl },
            { "Year", DateTime.UtcNow.Year.ToString() },
            { "Header", GetEmailHeader() }
        };

        var htmlBody = ReplacePlaceholders(template, placeholders);

        return new EmailMessage(
            To: to,
            Subject: $"Welcome to {AppName}!",
            Body: htmlBody,
            IsHtml: true
        );
    }

    // ========== BOOKING STATUS EMAILS ==========

    /// <summary>
    /// Generate booking confirmation email HTML
    /// </summary>
    public EmailMessage CreateBookingConfirmationEmail(
        string to,
        string customerName,
        string bookingNumber,
        string pickupLocation,
        string dropoffLocation,
        DateTime scheduleDate,
        string truckType,
        string cargoDescription)
    {
        var trackingUrl = $"{_baseUrl}/track?booking={Uri.EscapeDataString(bookingNumber)}";
        
        var template = LoadTemplate("booking-confirmation-email.html");
        var placeholders = new Dictionary<string, string>
        {
            { "AppName", AppName },
            { "CustomerName", customerName },
            { "BookingNumber", bookingNumber },
            { "PickupLocation", pickupLocation },
            { "DropoffLocation", dropoffLocation },
            { "ScheduleDate", scheduleDate.ToString("MMMM dd, yyyy 'at' HH:mm") + " UTC" },
            { "TruckType", truckType },
            { "CargoDescription", cargoDescription },
            { "TrackingUrl", trackingUrl },
            { "Year", DateTime.UtcNow.Year.ToString() },
            { "Header", GetEmailHeader() }
        };

        var htmlBody = ReplacePlaceholders(template, placeholders);

        return new EmailMessage(
            To: to,
            Subject: $"Booking Confirmed - {bookingNumber} - {AppName}",
            Body: htmlBody,
            IsHtml: true
        );
    }

    /// <summary>
    /// Generate booking dispatched (driver assigned) email HTML
    /// </summary>
    public EmailMessage CreateBookingDispatchedEmail(
        string to,
        string customerName,
        string bookingNumber,
        string pickupLocation,
        string dropoffLocation,
        DateTime scheduleDate)
    {
        var trackingUrl = $"{_baseUrl}/track?booking={Uri.EscapeDataString(bookingNumber)}";
        
        var template = LoadTemplate("booking-dispatched-email.html");
        var placeholders = new Dictionary<string, string>
        {
            { "AppName", AppName },
            { "CustomerName", customerName },
            { "BookingNumber", bookingNumber },
            { "PickupLocation", pickupLocation },
            { "DropoffLocation", dropoffLocation },
            { "ScheduleDate", scheduleDate.ToString("MMMM dd, yyyy 'at' HH:mm") + " UTC" },
            { "TrackingUrl", trackingUrl },
            { "Year", DateTime.UtcNow.Year.ToString() },
            { "Header", GetEmailHeader() }
        };

        var htmlBody = ReplacePlaceholders(template, placeholders);

        return new EmailMessage(
            To: to,
            Subject: $"Driver Assigned - {bookingNumber} - {AppName}",
            Body: htmlBody,
            IsHtml: true
        );
    }

    /// <summary>
    /// Generate booking in-progress (cargo picked up) email HTML
    /// </summary>
    public EmailMessage CreateBookingInProgressEmail(
        string to,
        string customerName,
        string bookingNumber,
        string dropoffLocation)
    {
        var trackingUrl = $"{_baseUrl}/track?booking={Uri.EscapeDataString(bookingNumber)}";
        
        var template = LoadTemplate("booking-in-progress-email.html");
        var placeholders = new Dictionary<string, string>
        {
            { "AppName", AppName },
            { "CustomerName", customerName },
            { "BookingNumber", bookingNumber },
            { "DropoffLocation", dropoffLocation },
            { "TrackingUrl", trackingUrl },
            { "Year", DateTime.UtcNow.Year.ToString() },
            { "Header", GetEmailHeader() }
        };

        var htmlBody = ReplacePlaceholders(template, placeholders);

        return new EmailMessage(
            To: to,
            Subject: $"Cargo Picked Up - {bookingNumber} - {AppName}",
            Body: htmlBody,
            IsHtml: true
        );
    }

    /// <summary>
    /// Generate booking delivered email HTML
    /// </summary>
    public EmailMessage CreateBookingDeliveredEmail(
        string to,
        string customerName,
        string bookingNumber,
        string dropoffLocation,
        DateTime deliveredAt)
    {
        var template = LoadTemplate("booking-delivered-email.html");
        var placeholders = new Dictionary<string, string>
        {
            { "AppName", AppName },
            { "CustomerName", customerName },
            { "BookingNumber", bookingNumber },
            { "DropoffLocation", dropoffLocation },
            { "DeliveredAt", deliveredAt.ToString("MMMM dd, yyyy 'at' HH:mm") + " UTC" },
            { "BaseUrl", _baseUrl },
            { "Year", DateTime.UtcNow.Year.ToString() },
            { "Header", GetEmailHeader() }
        };

        var htmlBody = ReplacePlaceholders(template, placeholders);

        return new EmailMessage(
            To: to,
            Subject: $"Delivery Completed - {bookingNumber} - {AppName}",
            Body: htmlBody,
            IsHtml: true
        );
    }

    /// <summary>
    /// Generate booking cancelled email HTML
    /// </summary>
    public EmailMessage CreateBookingCancelledEmail(
        string to,
        string customerName,
        string bookingNumber,
        string pickupLocation,
        string dropoffLocation,
        DateTime cancelledAt)
    {
        var template = LoadTemplate("booking-cancelled-email.html");
        var placeholders = new Dictionary<string, string>
        {
            { "AppName", AppName },
            { "CustomerName", customerName },
            { "BookingNumber", bookingNumber },
            { "PickupLocation", pickupLocation },
            { "DropoffLocation", dropoffLocation },
            { "CancelledAt", cancelledAt.ToString("MMMM dd, yyyy 'at' HH:mm") + " UTC" },
            { "BaseUrl", _baseUrl },
            { "Year", DateTime.UtcNow.Year.ToString() },
            { "Header", GetEmailHeader() }
        };

        var htmlBody = ReplacePlaceholders(template, placeholders);

        return new EmailMessage(
            To: to,
            Subject: $"Booking Cancelled - {bookingNumber} - {AppName}",
            Body: htmlBody,
            IsHtml: true
        );
    }

    /// <summary>
    /// Generate withdrawal receipt email (driver payout submitted).
    /// </summary>
    public EmailMessage CreateWithdrawalReceiptEmail(
        string to,
        string driverName,
        decimal amount,
        DateTime requestedAt,
        string maskedAccountNumber,
        string bankName,
        string? xenditPayoutId = null,
        string? xenditPayoutUrl = null)
    {
        var xenditReferenceRow = string.IsNullOrEmpty(xenditPayoutId)
            ? ""
            : $"<tr><td style=\"padding: 8px 0; color: #666; font-size: 14px;\">Reference (Xendit):</td><td style=\"padding: 8px 0; color: #1c190d; font-size: 14px;\">{System.Net.WebUtility.HtmlEncode(xenditPayoutId)}</td></tr>";
        var xenditLinkBlock = !string.IsNullOrEmpty(xenditPayoutUrl)
            ? $"<p style=\"margin: 16px 0 0 0; font-size: 14px;\"><a href=\"{System.Net.WebUtility.HtmlEncode(xenditPayoutUrl)}\" style=\"color: #007bff;\">View transaction status at Xendit</a></p>"
            : "";

        var template = LoadTemplate("withdrawal-receipt-email.html");
        var placeholders = new Dictionary<string, string>
        {
            { "AppName", AppName },
            { "DriverName", driverName },
            { "Amount", amount.ToString("N2") + " PHP" },
            { "RequestedAt", requestedAt.ToString("MMMM dd, yyyy 'at' HH:mm") + " UTC" },
            { "MaskedAccountNumber", maskedAccountNumber },
            { "BankName", bankName },
            { "XenditReferenceRow", xenditReferenceRow },
            { "XenditLinkBlock", xenditLinkBlock },
            { "BaseUrl", _baseUrl },
            { "Year", DateTime.UtcNow.Year.ToString() },
            { "Header", GetEmailHeader() }
        };

        var htmlBody = ReplacePlaceholders(template, placeholders);

        return new EmailMessage(
            To: to,
            Subject: $"Withdrawal Receipt - {amount:N2} PHP - {AppName}",
            Body: htmlBody,
            IsHtml: true
        );
    }

    /// <summary>
    /// Generate payment receipt email (customer payment for delivery).
    /// </summary>
    public EmailMessage CreatePaymentReceiptEmail(
        string to,
        string customerName,
        string paymentNumber,
        decimal amount,
        string currency,
        string? invoiceUrl,
        DateTime paidAt)
    {
        var template = LoadTemplate("payment-receipt-email.html");
        var invoiceLinkBlock = !string.IsNullOrEmpty(invoiceUrl)
            ? $"<p style=\"margin: 16px 0 0 0; font-size: 14px;\"><a href=\"{System.Net.WebUtility.HtmlEncode(invoiceUrl)}\" style=\"color: #007bff;\">View payment details / invoice</a></p>"
            : "";
        var placeholders = new Dictionary<string, string>
        {
            { "AppName", AppName },
            { "CustomerName", customerName },
            { "PaymentNumber", paymentNumber },
            { "Amount", amount.ToString("N2") + " " + currency },
            { "PaidAt", paidAt.ToString("MMMM dd, yyyy 'at' HH:mm") + " UTC" },
            { "InvoiceLinkBlock", invoiceLinkBlock },
            { "BaseUrl", _baseUrl },
            { "Year", DateTime.UtcNow.Year.ToString() },
            { "Header", GetEmailHeader() }
        };

        var htmlBody = ReplacePlaceholders(template, placeholders);

        return new EmailMessage(
            To: to,
            Subject: $"Payment Receipt - {paymentNumber} - {AppName}",
            Body: htmlBody,
            IsHtml: true
        );
    }

    // ========== DRIVER APPLICATION EMAILS ==========

    /// <summary>
    /// Generate driver application approved email (account active, driver can start accepting bookings).
    /// </summary>
    public EmailMessage CreateDriverApplicationApprovedEmail(string to, string fullName)
    {
        var template = LoadTemplate("driver-application-approved-email.html");
        var placeholders = new Dictionary<string, string>
        {
            { "AppName", AppName },
            { "FullName", fullName },
            { "BaseUrl", _baseUrl },
            { "Year", DateTime.UtcNow.Year.ToString() },
            { "Header", GetEmailHeader() }
        };

        var htmlBody = ReplacePlaceholders(template, placeholders);

        return new EmailMessage(
            To: to,
            Subject: $"Driver Application Approved - {AppName}",
            Body: htmlBody,
            IsHtml: true
        );
    }

    /// <summary>
    /// Generate driver application rejected email. Reviewer notes are included as the reason only when provided.
    /// </summary>
    public EmailMessage CreateDriverApplicationRejectedEmail(string to, string fullName, string? notes)
    {
        var reasonBlock = string.IsNullOrWhiteSpace(notes)
            ? ""
            : $@"<div style=""background-color: #f8f9fa; border-left: 4px solid #FFD700; border-radius: 4px; padding: 15px 20px; margin: 0 0 20px 0;"">
                                <p style=""color: #1c190d; font-size: 14px; font-weight: 600; margin: 0 0 4px 0;"">Reason</p>
                                <p style=""color: #4a4a4a; font-size: 14px; line-height: 1.6; margin: 0;"">{System.Net.WebUtility.HtmlEncode(notes)}</p>
                            </div>";

        var template = LoadTemplate("driver-application-rejected-email.html");
        var placeholders = new Dictionary<string, string>
        {
            { "AppName", AppName },
            { "FullName", fullName },
            { "ReasonBlock", reasonBlock },
            { "BaseUrl", _baseUrl },
            { "Year", DateTime.UtcNow.Year.ToString() },
            { "Header", GetEmailHeader() }
        };

        var htmlBody = ReplacePlaceholders(template, placeholders);

        return new EmailMessage(
            To: to,
            Subject: $"Driver Application Update - {AppName}",
            Body: htmlBody,
            IsHtml: true
        );
    }
}
