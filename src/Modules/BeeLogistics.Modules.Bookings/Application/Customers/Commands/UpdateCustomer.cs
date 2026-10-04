using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MediatR;
using Microsoft.AspNetCore.Identity;
using BeeLogistics.Modules.Identity.Domain;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record UpdateCustomerCommand(Guid Id, UpdateCustomerDto Dto) : IRequest<Result<CustomerDto>>;

public class UpdateCustomerCommandHandler : IRequestHandler<UpdateCustomerCommand, Result<CustomerDto>>
{
    private readonly ICustomerRepository _repository;
    private readonly UserManager<ApplicationUser> _userManager;
    
    public UpdateCustomerCommandHandler(ICustomerRepository repository, UserManager<ApplicationUser> userManager)
    {
        _repository = repository;
        _userManager = userManager;
    }

    public async Task<Result<CustomerDto>> Handle(UpdateCustomerCommand request, CancellationToken ct)
    {
        var customer = await _repository.GetByIdAsync(request.Id, ct);
        if (customer == null) return Result.NotFound<CustomerDto>("Customer not found");

        var dto = request.Dto;
        customer.UpdateProfile(dto.Name, dto.CompanyName, dto.Phone, dto.Address);
        await _repository.SaveChangesAsync(ct);

        // Look up email verification status
        var user = await _userManager.FindByEmailAsync(customer.Email);
        var isEmailVerified = user?.EmailConfirmed ?? false;

        return Result.Ok(CustomerMapper.ToDto(customer, isEmailVerified));
    }
}
