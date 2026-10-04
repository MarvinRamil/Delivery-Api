using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record DeleteCustomerCommand(Guid Id) : IRequest<Result>;

public class DeleteCustomerCommandHandler : IRequestHandler<DeleteCustomerCommand, Result>
{
    private readonly ICustomerRepository _repository;
    public DeleteCustomerCommandHandler(ICustomerRepository repository) => _repository = repository;

    public async Task<Result> Handle(DeleteCustomerCommand request, CancellationToken ct)
    {
        var customer = await _repository.GetByIdAsync(request.Id, ct);
        if (customer == null) return Result.Fail("Customer not found");

        customer.Deactivate(); // Soft delete
        await _repository.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
