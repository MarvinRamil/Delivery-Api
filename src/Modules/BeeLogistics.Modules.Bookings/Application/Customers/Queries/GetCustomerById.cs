using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MediatR;
using Microsoft.AspNetCore.Identity;
using BeeLogistics.Modules.Identity.Domain;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record GetCustomerByIdQuery(Guid Id) : IRequest<Result<CustomerDto>>;

public class GetCustomerByIdQueryHandler : IRequestHandler<GetCustomerByIdQuery, Result<CustomerDto>>
{
    private readonly ICustomerRepository _repository;
    private readonly UserManager<ApplicationUser> _userManager;
    
    public GetCustomerByIdQueryHandler(ICustomerRepository repository, UserManager<ApplicationUser> userManager)
    {
        _repository = repository;
        _userManager = userManager;
    }

    public async Task<Result<CustomerDto>> Handle(GetCustomerByIdQuery request, CancellationToken ct)
    {
        var customer = await _repository.GetByIdAsync(request.Id, ct);
        if (customer == null) return Result.NotFound<CustomerDto>("Customer not found");

        // Look up email verification status
        var user = await _userManager.FindByEmailAsync(customer.Email);
        var isEmailVerified = user?.EmailConfirmed ?? false;

        return Result.Ok(CustomerMapper.ToDto(customer, isEmailVerified));
    }
}
