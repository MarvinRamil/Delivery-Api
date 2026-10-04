using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Shared.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Resolves a Bookings-module CustomerId to the corresponding ASP.NET Identity UserId.
/// Looks up the Customer by ID to get their email, then finds the Identity user by email.
/// </summary>
public class CustomerIdentityResolver : ICustomerIdentityResolver
{
    private readonly ICustomerRepository _customerRepository;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<CustomerIdentityResolver> _logger;

    public CustomerIdentityResolver(
        ICustomerRepository customerRepository,
        UserManager<ApplicationUser> userManager,
        ILogger<CustomerIdentityResolver> logger)
    {
        _customerRepository = customerRepository;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<string?> ResolveIdentityUserIdAsync(Guid bookingCustomerId, CancellationToken ct = default)
    {
        try
        {
            var customer = await _customerRepository.GetByIdAsync(bookingCustomerId, ct);
            if (customer == null || string.IsNullOrEmpty(customer.Email))
            {
                _logger.LogWarning("[CustomerIdentityResolver] Customer {CustomerId} not found or has no email", bookingCustomerId);
                return null;
            }

            var user = await _userManager.FindByEmailAsync(customer.Email);
            if (user == null)
            {
                _logger.LogWarning("[CustomerIdentityResolver] No Identity user found for email {Email} (CustomerId: {CustomerId})", customer.Email, bookingCustomerId);
                return null;
            }

            return user.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CustomerIdentityResolver] Failed to resolve identity for CustomerId {CustomerId}", bookingCustomerId);
            return null;
        }
    }

    public async Task<Guid?> ResolveBookingCustomerIdAsync(string identityUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(identityUserId))
            return null;

        try
        {
            var user = await _userManager.FindByIdAsync(identityUserId);
            if (user?.Email == null)
            {
                _logger.LogWarning("[CustomerIdentityResolver] No Identity user (or no email) for UserId {UserId}", identityUserId);
                return null;
            }

            var customer = await _customerRepository.GetByEmailAsync(user.Email, ct);
            if (customer == null)
            {
                // Expected for a user who has never booked - the Customer row is created on first booking.
                _logger.LogWarning("[CustomerIdentityResolver] No Customer row for Identity user {UserId}", identityUserId);
                return null;
            }

            return customer.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CustomerIdentityResolver] Failed to resolve CustomerId for Identity user {UserId}", identityUserId);
            return null;
        }
    }
}
