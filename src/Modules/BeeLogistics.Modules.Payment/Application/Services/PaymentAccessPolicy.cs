using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Shared.Contracts;

namespace BeeLogistics.Modules.Payment.Application.Services;

/// <summary>
/// Grants a payment write only to the owning customer or an elevated (backoffice) caller.
/// Closes the IDOR on create and link-booking, which previously acted on any payment and any
/// booking for any authenticated caller.
/// </summary>
public sealed class PaymentAccessPolicy : IPaymentAccessPolicy
{
    private readonly IBookingOwnershipVerifier _bookingOwnership;

    public PaymentAccessPolicy(IBookingOwnershipVerifier bookingOwnership)
        => _bookingOwnership = bookingOwnership;

    public bool CanActForCustomer(Guid paymentCustomerId, Guid callerUserId, bool isElevated)
    {
        if (isElevated)
            return true;

        if (paymentCustomerId == Guid.Empty || callerUserId == Guid.Empty)
            return false;

        // Payment.CustomerId is the Identity UserId, so this is a direct comparison - no
        // Customer-row resolution needed here (unlike the booking side).
        return paymentCustomerId == callerUserId;
    }

    public async Task<bool> CanLinkToBookingAsync(Guid bookingId, Guid callerUserId, bool isElevated, CancellationToken ct = default)
    {
        if (isElevated)
            return true;

        return await _bookingOwnership.IsOwnedByCustomerAsync(bookingId, callerUserId, ct);
    }
}
