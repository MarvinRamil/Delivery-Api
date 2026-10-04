using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record GetVehiclePricingByIdQuery(Guid Id) : IRequest<Result<VehiclePricingDto>>;

public class GetVehiclePricingByIdQueryHandler : IRequestHandler<GetVehiclePricingByIdQuery, Result<VehiclePricingDto>>
{
    private readonly IVehiclePricingRepository _repository;

    public GetVehiclePricingByIdQueryHandler(IVehiclePricingRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<VehiclePricingDto>> Handle(GetVehiclePricingByIdQuery request, CancellationToken ct)
    {
        var pricing = await _repository.GetByIdAsync(request.Id, ct);
        if (pricing == null)
            return Result.Fail<VehiclePricingDto>("Vehicle pricing not found");

        var dto = VehiclePricingMapper.ToDto(pricing);

        return Result.Ok(dto);
    }
}
