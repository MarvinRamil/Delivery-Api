using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

// Queries
public record GetVehiclePricingsQuery : IRequest<Result<IReadOnlyList<VehiclePricingDto>>>;

// Handlers
public class GetVehiclePricingsQueryHandler : IRequestHandler<GetVehiclePricingsQuery, Result<IReadOnlyList<VehiclePricingDto>>>
{
    private readonly IVehiclePricingRepository _repository;

    public GetVehiclePricingsQueryHandler(IVehiclePricingRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<VehiclePricingDto>>> Handle(GetVehiclePricingsQuery request, CancellationToken ct)
    {
        var pricings = await _repository.GetActivePricingsAsync(ct);
        var dtos = pricings.Select(VehiclePricingMapper.ToDto).ToList();
        return Result.Ok<IReadOnlyList<VehiclePricingDto>>(dtos);
    }
}
