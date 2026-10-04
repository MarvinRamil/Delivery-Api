using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.DTOs;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using BeeLogistics.Modules.Identity.Domain;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

// Queries
public record GetCustomersQuery(int Page = 1, int PageSize = 50) : IRequest<Result<PagedResult<CustomerDto>>>;

// Handlers
public class GetCustomersQueryHandler : IRequestHandler<GetCustomersQuery, Result<PagedResult<CustomerDto>>>
{
    private readonly ICustomerRepository _repository;
    private readonly UserManager<ApplicationUser> _userManager;

    public GetCustomersQueryHandler(ICustomerRepository repository, UserManager<ApplicationUser> userManager)
    {
        _repository = repository;
        _userManager = userManager;
    }

    public async Task<Result<PagedResult<CustomerDto>>> Handle(GetCustomersQuery request, CancellationToken ct)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? 10 : request.PageSize > 500 ? 500 : request.PageSize;

        var (customers, totalCount) = await _repository.GetPagedAsync(page, pageSize, ct);
        if (customers.Count == 0)
        {
            var empty = new PagedResult<CustomerDto>(new List<CustomerDto>(), 0, page, pageSize);
            return Result.Ok(empty);
        }

        var emails = customers.Select(c => c.Email).Distinct().ToList();
        var verifiedByEmail = await _userManager.Users
            .Where(u => emails.Contains(u.Email))
            .Select(u => new { u.Email, u.EmailConfirmed })
            .ToDictionaryAsync(x => x.Email, x => x.EmailConfirmed, ct);

        var dtos = customers.Select(customer =>
            CustomerMapper.ToMaskedDto(customer, verifiedByEmail.GetValueOrDefault(customer.Email, false))
        ).ToList();

        var pagedResult = new PagedResult<CustomerDto>(dtos, totalCount, page, pageSize);
        return Result.Ok(pagedResult);
    }
}
