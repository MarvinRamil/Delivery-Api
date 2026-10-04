using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Abstractions;
using MediatR;
using Microsoft.AspNetCore.Identity;
using BeeLogistics.Modules.Identity.Domain;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

// Commands
public record CreateCustomerCommand(CreateCustomerDto Dto) : IRequest<Result<CustomerDto>>;

public class CreateCustomerCommandHandler : IRequestHandler<CreateCustomerCommand, Result<CustomerDto>>
{
    private readonly ICustomerRepository _repository;
    private readonly UserManager<ApplicationUser> _userManager;
    
    public CreateCustomerCommandHandler(ICustomerRepository repository, UserManager<ApplicationUser> userManager)
    {
        _repository = repository;
        _userManager = userManager;
    }

    public async Task<Result<CustomerDto>> Handle(CreateCustomerCommand request, CancellationToken ct)
    {
        var dto = request.Dto;
        if (await _repository.ExistsAsync(c => c.Email == dto.Email, ct))
            return Result.Fail<CustomerDto>("Customer with this email already exists");

        var customer = new Customer(dto.Name, dto.Email, dto.CompanyName, dto.Phone, dto.Address);
        _repository.Add(customer);
        await _repository.SaveChangesAsync(ct);

        // Look up email verification status
        var user = await _userManager.FindByEmailAsync(customer.Email);
        var isEmailVerified = user?.EmailConfirmed ?? false;

        return Result.Ok(CustomerMapper.ToDto(customer, isEmailVerified));
    }
}
