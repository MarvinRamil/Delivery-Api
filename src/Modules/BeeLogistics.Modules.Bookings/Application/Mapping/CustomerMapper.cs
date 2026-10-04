using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Domain;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

// Helper
internal static class CustomerMapper
{
    /// <summary>
    /// Full customer detail. Used by the single-customer and write endpoints, which return
    /// email, phone and address in the clear.
    /// </summary>
    public static CustomerDto ToDto(Customer customer, bool isEmailVerified) =>
        new(
            customer.Id, customer.Name, customer.Email, customer.CompanyName,
            customer.Phone, customer.Address, customer.IsActive, isEmailVerified,
            customer.CreatedAt, customer.UpdatedAt
        );

    /// <summary>
    /// Customer detail with email, phone and address masked. Used only by the paged list.
    /// The divergence from <see cref="ToDto"/> is deliberate and is what the list endpoint
    /// has always returned - do not collapse the two without checking what the back-office
    /// customer table renders.
    /// </summary>
    public static CustomerDto ToMaskedDto(Customer customer, bool isEmailVerified) =>
        new(
            customer.Id, customer.Name, MaskEmail(customer.Email), customer.CompanyName,
            MaskPhone(customer.Phone), MaskAddress(customer.Address), customer.IsActive, isEmailVerified,
            customer.CreatedAt, customer.UpdatedAt
        );

    private static string MaskEmail(string email)
    {
        if (string.IsNullOrEmpty(email)) return email;
        var parts = email.Split('@');
        if (parts.Length != 2) return email;
        var username = parts[0];
        var domain = parts[1];
        return $"{username[0]}***@{domain}";
    }

    private static string MaskPhone(string phone)
    {
        if (string.IsNullOrEmpty(phone)) return phone;
        if (phone.Length <= 4) return "***";
        return $"*** *** {phone.Substring(phone.Length - 4)}";
    }

    private static string MaskAddress(string address)
    {
        if (string.IsNullOrEmpty(address)) return address;
        // Show only city if comma exists, otherwise show first few chars
        var parts = address.Split(',');
        if (parts.Length > 1) return parts.Last().Trim();
        return address.Length > 20 ? $"{address.Substring(0, 20)}..." : address;
    }
}
