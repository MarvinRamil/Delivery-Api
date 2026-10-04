using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record GetVehiclePricingVersionsQuery(Guid VehiclePricingId) : IRequest<Result<IReadOnlyList<VehiclePricingVersionDto>>>;

public class GetVehiclePricingVersionsQueryHandler : IRequestHandler<GetVehiclePricingVersionsQuery, Result<IReadOnlyList<VehiclePricingVersionDto>>>
{
    private readonly IVehiclePricingRepository _repository;

    public GetVehiclePricingVersionsQueryHandler(IVehiclePricingRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<VehiclePricingVersionDto>>> Handle(GetVehiclePricingVersionsQuery request, CancellationToken ct)
    {
        var versions = await _repository.GetVersionsByVehiclePricingIdAsync(request.VehiclePricingId, ct);
        var dtos = versions.Select(v => new VehiclePricingVersionDto(
            v.Id,
            v.VehiclePricingId,
            v.Version,
            v.BaseFare,
            v.PerKm0to5,
            v.PerKmAbove5,
            v.AdditionalStopFee,
            v.WeightLimitKg,
            v.WeightSurchargePerKg,
            v.SizeLimit,
            v.Types,
            v.LongDistanceBaseFare,
            v.LongDistancePerKm41to60,
            v.LongDistancePerKmAbove60,
            v.SurchargeInfo,
            v.Remarks,
            v.ChangedByUserId,
            v.ChangedByUserName,
            v.CreatedAt
        )).ToList();

        return Result.Ok<IReadOnlyList<VehiclePricingVersionDto>>(dtos);
    }
}
