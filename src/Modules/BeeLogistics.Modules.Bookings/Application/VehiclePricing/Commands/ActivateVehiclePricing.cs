using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record ActivateVehiclePricingCommand(Guid Id) : IRequest<Result>;

public class ActivateVehiclePricingCommandHandler : IRequestHandler<ActivateVehiclePricingCommand, Result>
{
    private readonly IVehiclePricingRepository _repository;

    public ActivateVehiclePricingCommandHandler(IVehiclePricingRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result> Handle(ActivateVehiclePricingCommand request, CancellationToken ct)
    {
        var pricing = await _repository.GetByIdAsync(request.Id, ct);
        if (pricing == null)
            return Result.Fail("Vehicle pricing not found");

        pricing.Activate();
        _repository.Update(pricing);
        await _repository.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
