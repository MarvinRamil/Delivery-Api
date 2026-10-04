using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

// CallerUserId is the Identity user id from the JWT subject and IsElevated is the resolved
// backoffice policy result: without them the handler has nothing to check ownership against.
public record GetBookingByIdQuery(Guid Id, Guid CallerUserId, bool IsElevated) : IRequest<Result<BookingDto>>;

public class GetBookingByIdQueryHandler : IRequestHandler<GetBookingByIdQuery, Result<BookingDto>>
{
    private readonly IBookingRepository _repository;
    private readonly IDriverDisplayInfoProvider _driverDisplayInfoProvider;
    private readonly IFileStorageService _fileStorage;
    private readonly IBookingAccessPolicy _accessPolicy;

    public GetBookingByIdQueryHandler(
        IBookingRepository repository,
        IDriverDisplayInfoProvider driverDisplayInfoProvider,
        IFileStorageService fileStorage,
        IBookingAccessPolicy accessPolicy)
    {
        _repository = repository;
        _driverDisplayInfoProvider = driverDisplayInfoProvider;
        _fileStorage = fileStorage;
        _accessPolicy = accessPolicy;
    }

    public async Task<Result<BookingDto>> Handle(GetBookingByIdQuery request, CancellationToken ct)
    {
        var booking = await _repository.GetByIdWithProofOfDeliveriesAsync(request.Id, ct);
        if (booking == null) return Result.NotFound<BookingDto>("Booking not found");

        if (!await _accessPolicy.CanAccessAsync(booking, request.CallerUserId, request.IsElevated, ct))
            return Result.Forbidden<BookingDto>("You do not have access to this booking");

        DriverDisplayInfo? driverInfo = null;
        if (booking.SelectedDriverId.HasValue)
            driverInfo = await _driverDisplayInfoProvider.GetAsync(booking.SelectedDriverId.Value, ct);

        var dto = BookingMapper.ToDto(booking, driverInfo);
        var resolved = await BookingImageUrlResolver.ResolveAsync(dto, _fileStorage, ct);
        return Result.Ok(resolved);
    }
}
