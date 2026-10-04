using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Bookings.Infrastructure;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

// Commands
public record CreateVehiclePricingCommand(CreateVehiclePricingDto Dto, Guid? UserId, string? UserName) : IRequest<Result<VehiclePricingDto>>;

public class CreateVehiclePricingCommandHandler : IRequestHandler<CreateVehiclePricingCommand, Result<VehiclePricingDto>>
{
    private readonly IVehiclePricingRepository _repository;
    private readonly BookingsDbContext _context;

    public CreateVehiclePricingCommandHandler(IVehiclePricingRepository repository, BookingsDbContext context)
    {
        _repository = repository;
        _context = context;
    }

    public async Task<Result<VehiclePricingDto>> Handle(CreateVehiclePricingCommand request, CancellationToken ct)
    {
        var dto = request.Dto;

        // Check if vehicle type already exists
        var existing = await _repository.GetByVehicleTypeAsync(dto.VehicleType, ct);
        if (existing != null)
            return Result.Fail<VehiclePricingDto>("Vehicle pricing for this vehicle type already exists");

        var pricing = new VehiclePricing(
            dto.VehicleType,
            dto.BaseFare,
            dto.PerKm0to5,
            dto.PerKmAbove5,
            dto.AdditionalStopFee,
            dto.WeightLimitKg,
            dto.WeightSurchargePerKg,
            dto.SizeLimit,
            dto.Types,
            dto.LongDistanceBaseFare,
            dto.LongDistancePerKm41to60,
            dto.LongDistancePerKmAbove60,
            dto.SurchargeInfo,
            dto.Remarks,
            request.UserId,
            request.UserName
        );

        // Create initial version
        var initialVersion = new VehiclePricingVersion(
            pricing.Id,
            1,
            pricing.BaseFare,
            pricing.PerKm0to5,
            pricing.PerKmAbove5,
            pricing.AdditionalStopFee,
            pricing.WeightLimitKg,
            pricing.WeightSurchargePerKg,
            pricing.SizeLimit,
            pricing.Types,
            pricing.LongDistanceBaseFare,
            pricing.LongDistancePerKm41to60,
            pricing.LongDistancePerKmAbove60,
            pricing.SurchargeInfo,
            pricing.Remarks,
            request.UserId,
            request.UserName
        );

        _context.Set<VehiclePricingVersion>().Add(initialVersion);
        _repository.Add(pricing);
        await _repository.SaveChangesAsync(ct);

        var resultDto = VehiclePricingMapper.ToDto(pricing);

        return Result.Ok(resultDto);
    }
}
