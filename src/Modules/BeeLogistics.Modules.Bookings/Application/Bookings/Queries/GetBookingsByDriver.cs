using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record GetBookingsByDriverQuery(Guid DriverId) : IRequest<Result<IReadOnlyList<BookingDto>>>;

public class GetBookingsByDriverQueryHandler : IRequestHandler<GetBookingsByDriverQuery, Result<IReadOnlyList<BookingDto>>>
{
    private readonly IBookingRepository _repository;
    private readonly IFileStorageService _fileStorage;

    public GetBookingsByDriverQueryHandler(IBookingRepository repository, IFileStorageService fileStorage)
    {
        _repository = repository;
        _fileStorage = fileStorage;
    }

    public async Task<Result<IReadOnlyList<BookingDto>>> Handle(GetBookingsByDriverQuery request, CancellationToken ct)
    {
        var bookings = await _repository.GetByDriverIdAsync(request.DriverId, ct);
        var dtos = bookings.Select(BookingMapper.ToDto).ToList();
        var resolved = await BookingImageUrlResolver.ResolveListAsync(dtos, _fileStorage, ct);
        return Result.Ok(resolved);
    }
}
