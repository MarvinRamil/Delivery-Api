using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

/// <summary>
/// Answers <see cref="BeeLogistics.Shared.Contracts.GetActiveVehicleTypesQuery"/> for other
/// modules — currently the Drivers module, validating which vehicle type a driver may
/// register under. Bookings owns the vehicle pricing table, so it is the only module that
/// can answer this; going through Shared.Contracts + MediatR keeps that ownership intact
/// without Drivers referencing this module.
///
/// Returns names only, never pricing rows: what a class costs stays inside this module.
/// </summary>
public class GetActiveVehicleTypesQueryHandler : IRequestHandler<BeeLogistics.Shared.Contracts.GetActiveVehicleTypesQuery, Result<IReadOnlyList<string>>>
{
    private readonly IVehiclePricingRepository _repository;

    public GetActiveVehicleTypesQueryHandler(IVehiclePricingRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<string>>> Handle(BeeLogistics.Shared.Contracts.GetActiveVehicleTypesQuery request, CancellationToken ct)
    {
        // GetActivePricingsAsync already filters to active, non-deleted rows ordered by
        // VehicleType, so the ordering the contract promises holds here.
        var pricings = await _repository.GetActivePricingsAsync(ct);
        return Result.Ok<IReadOnlyList<string>>(pricings.Select(p => p.VehicleType).ToList());
    }
}
