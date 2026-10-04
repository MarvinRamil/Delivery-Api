using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MediatR;
using Microsoft.AspNetCore.Identity;
using BeeLogistics.Modules.Identity.Domain;
using CustomerDtoSales = BeeLogistics.Modules.Bookings.Application.DTOs.CustomerDto;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public class GetCustomerByEmailQueryHandler : IRequestHandler<BeeLogistics.Shared.Contracts.GetCustomerByEmailQuery, Result<object>>
{
    private readonly ICustomerRepository _repository;
    private readonly UserManager<ApplicationUser> _userManager;
    
    public GetCustomerByEmailQueryHandler(ICustomerRepository repository, UserManager<ApplicationUser> userManager)
    {
        _repository = repository;
        _userManager = userManager;
    }

    public async Task<Result<object>> Handle(BeeLogistics.Shared.Contracts.GetCustomerByEmailQuery request, CancellationToken ct)
    {
        var customer = await _repository.GetByEmailAsync(request.Email, ct);
        if (customer == null) return Result.NotFound<object>("Customer not found");

        // Look up email verification status
        var user = await _userManager.FindByEmailAsync(customer.Email);
        var isEmailVerified = user?.EmailConfirmed ?? false;

        var customerDto = new CustomerDtoSales(
            customer.Id, customer.Name, customer.Email, customer.CompanyName,
            customer.Phone, customer.Address, customer.IsActive, isEmailVerified, 
            customer.CreatedAt, customer.UpdatedAt
        );
        return Result.Ok<object>(customerDto);
    }
}
