namespace BeeLogistics.Modules.Payment.Application.DTOs;

public record PaymentDto(
    Guid Id,
    string PaymentNumber,
    Guid? BookingId,
    Guid CustomerId,
    decimal Amount,
    string Currency,
    string Status,
    string Method,
    string? XenditInvoiceUrl,
    DateTime? PaidAt,
    DateTime CreatedAt
);

public record CreatePaymentDto(
    Guid? BookingId,  // Optional: null for PayOnline flow where payment is created before booking
    Guid CustomerId,
    decimal Amount,
    string PayerEmail,
    string Description,
    string Method = "BankTransfer",
    string Currency = "PHP"
);

public record RefundPaymentRequest(
    Guid BookingId,
    Guid? DriverId = null,
    string? Reason = null,
    decimal? Amount = null,
    bool BypassTimeLimit = false
);

public record WebhookPayload(
    string Id,
    string ExternalId,
    string Status,
    decimal Amount,
    DateTime? PaidAt
);
