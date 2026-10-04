using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record DeactivateVehiclePricingCommand(Guid Id) : IRequest<Result>;

public class DeactivateVehiclePricingCommandHandler : IRequestHandler<DeactivateVehiclePricingCommand, Result>
{
    private readonly IVehiclePricingRepository _repository;

    public DeactivateVehiclePricingCommandHandler(IVehiclePricingRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result> Handle(DeactivateVehiclePricingCommand request, CancellationToken ct)
    {
        var pricing = await _repository.GetByIdAsync(request.Id, ct);
        if (pricing == null)
            return Result.Fail("Vehicle pricing not found");

        pricing.Deactivate();
        _repository.Update(pricing);
        await _repository.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
