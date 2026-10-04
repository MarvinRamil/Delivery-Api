using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record GetBookingsByCustomerQuery(Guid CustomerId) : IRequest<Result<IReadOnlyList<BookingDto>>>;

public class GetBookingsByCustomerQueryHandler : IRequestHandler<GetBookingsByCustomerQuery, Result<IReadOnlyList<BookingDto>>>
{
    private readonly IBookingRepository _repository;
    private readonly IDriverDisplayInfoProvider _driverDisplayInfoProvider;
    private readonly IFileStorageService _fileStorage;

    public GetBookingsByCustomerQueryHandler(IBookingRepository repository, IDriverDisplayInfoProvider driverDisplayInfoProvider, IFileStorageService fileStorage)
    {
        _repository = repository;
        _driverDisplayInfoProvider = driverDisplayInfoProvider;
        _fileStorage = fileStorage;
    }

    public async Task<Result<IReadOnlyList<BookingDto>>> Handle(GetBookingsByCustomerQuery request, CancellationToken ct)
    {
        var bookings = await _repository.GetByCustomerIdAsync(request.CustomerId, ct);
        var resolved = await BookingListProjector.ProjectAsync(bookings, _driverDisplayInfoProvider, _fileStorage, ct);
        return Result.Ok(resolved);
    }
}
