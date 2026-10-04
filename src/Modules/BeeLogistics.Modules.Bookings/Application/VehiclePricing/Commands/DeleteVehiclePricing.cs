using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record DeleteVehiclePricingCommand(Guid Id) : IRequest<Result>;

public class DeleteVehiclePricingCommandHandler : IRequestHandler<DeleteVehiclePricingCommand, Result>
{
    private readonly IVehiclePricingRepository _repository;

    public DeleteVehiclePricingCommandHandler(IVehiclePricingRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result> Handle(DeleteVehiclePricingCommand request, CancellationToken ct)
    {
        var pricing = await _repository.GetByIdAsync(request.Id, ct);
        if (pricing == null)
            return Result.Fail("Vehicle pricing not found");

        _repository.Remove(pricing);
        await _repository.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
