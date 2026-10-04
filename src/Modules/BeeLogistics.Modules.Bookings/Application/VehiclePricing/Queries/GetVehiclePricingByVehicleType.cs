using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record GetVehiclePricingByVehicleTypeQuery(string VehicleType) : IRequest<Result<VehiclePricingDto>>;

public class GetVehiclePricingByVehicleTypeQueryHandler : IRequestHandler<GetVehiclePricingByVehicleTypeQuery, Result<VehiclePricingDto>>
{
    private readonly IVehiclePricingRepository _repository;

    public GetVehiclePricingByVehicleTypeQueryHandler(IVehiclePricingRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<VehiclePricingDto>> Handle(GetVehiclePricingByVehicleTypeQuery request, CancellationToken ct)
    {
        var pricing = await _repository.GetByVehicleTypeAsync(request.VehicleType, ct);
        if (pricing == null)
            return Result.Fail<VehiclePricingDto>("Vehicle pricing not found");

        var dto = VehiclePricingMapper.ToDto(pricing);

        return Result.Ok(dto);
    }
}
