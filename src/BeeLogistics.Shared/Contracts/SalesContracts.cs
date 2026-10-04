using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Query to get a customer by email. Used for cross-module communication.
/// Implemented by Sales module, returns BeeLogistics.Modules.Sales.Application.DTOs.CustomerDto
/// </summary>
public record GetCustomerByEmailQuery(string Email) : IRequest<Result<object>>;
