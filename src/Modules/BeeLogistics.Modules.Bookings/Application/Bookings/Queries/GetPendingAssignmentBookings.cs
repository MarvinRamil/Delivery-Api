using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record GetPendingAssignmentBookingsQuery : IRequest<Result<IReadOnlyList<BookingDto>>>;

public class GetPendingAssignmentBookingsQueryHandler : IRequestHandler<GetPendingAssignmentBookingsQuery, Result<IReadOnlyList<BookingDto>>>
{
    private readonly IBookingRepository _repository;
    private readonly IFileStorageService _fileStorage;

    public GetPendingAssignmentBookingsQueryHandler(IBookingRepository repository, IFileStorageService fileStorage)
    {
        _repository = repository;
        _fileStorage = fileStorage;
    }

    public async Task<Result<IReadOnlyList<BookingDto>>> Handle(GetPendingAssignmentBookingsQuery request, CancellationToken ct)
    {
        var bookings = await _repository.GetPendingAssignmentBookingsAsync(ct);
        var dtos = bookings.Select(BookingMapper.ToDto).ToList();
        var resolved = await BookingImageUrlResolver.ResolveListAsync(dtos, _fileStorage, ct);
        return Result.Ok(resolved);
    }
}
