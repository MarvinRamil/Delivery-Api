using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.DTOs;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

// Queries
public record GetBookingsQuery(int Page = 1, int PageSize = 50) : IRequest<Result<PagedResult<BookingDto>>>;

// Handlers
public class GetBookingsQueryHandler : IRequestHandler<GetBookingsQuery, Result<PagedResult<BookingDto>>>
{
    private readonly IBookingRepository _repository;
    private readonly IFileStorageService _fileStorage;

    public GetBookingsQueryHandler(IBookingRepository repository, IFileStorageService fileStorage)
    {
        _repository = repository;
        _fileStorage = fileStorage;
    }

    public async Task<Result<PagedResult<BookingDto>>> Handle(GetBookingsQuery request, CancellationToken ct)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? 10 : request.PageSize > 500 ? 500 : request.PageSize;

        var (items, totalCount) = await _repository.GetPagedAsync(page, pageSize, ct);
        var dtos = items.Select(BookingMapper.ToDto).ToList();
        var resolved = await BookingImageUrlResolver.ResolveListAsync(dtos, _fileStorage, ct);
        var pagedResult = new PagedResult<BookingDto>(resolved.ToList(), totalCount, page, pageSize);
        return Result.Ok(pagedResult);
    }
}
