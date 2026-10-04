using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Bookings.Infrastructure;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record UpdateVehiclePricingCommand(Guid Id, UpdateVehiclePricingDto Dto, Guid? UserId, string? UserName) : IRequest<Result<VehiclePricingDto>>;

public class UpdateVehiclePricingCommandHandler : IRequestHandler<UpdateVehiclePricingCommand, Result<VehiclePricingDto>>
{
    private readonly IVehiclePricingRepository _repository;
    private readonly BookingsDbContext _context;

    public UpdateVehiclePricingCommandHandler(IVehiclePricingRepository repository, BookingsDbContext context)
    {
        _repository = repository;
        _context = context;
    }

    public async Task<Result<VehiclePricingDto>> Handle(UpdateVehiclePricingCommand request, CancellationToken ct)
    {
        var pricing = await _repository.GetByIdAsync(request.Id, ct);
        if (pricing == null)
            return Result.Fail<VehiclePricingDto>("Vehicle pricing not found");

        var dto = request.Dto;

        // Create version snapshot before updating
        var newVersion = new VehiclePricingVersion(
            pricing.Id,
            pricing.Version + 1,
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
            pricing.UpdatedByUserId,
            pricing.UpdatedByUserName
        );

        _context.Set<VehiclePricingVersion>().Add(newVersion);

        // Update pricing
        pricing.UpdatePricing(
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

        _repository.Update(pricing);
        await _repository.SaveChangesAsync(ct);

        var resultDto = VehiclePricingMapper.ToDto(pricing);

        return Result.Ok(resultDto);
    }
}
