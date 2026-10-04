using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record GetMyBookingsQuery(string UserEmail) : IRequest<Result<IReadOnlyList<BookingDto>>>;

public class GetMyBookingsQueryHandler : IRequestHandler<GetMyBookingsQuery, Result<IReadOnlyList<BookingDto>>>
{
    private readonly IBookingRepository _repository;
    private readonly ICustomerRepository _customerRepository;
    private readonly IDriverDisplayInfoProvider _driverDisplayInfoProvider;
    private readonly IFileStorageService _fileStorage;

    public GetMyBookingsQueryHandler(
        IBookingRepository repository,
        ICustomerRepository customerRepository,
        IDriverDisplayInfoProvider driverDisplayInfoProvider,
        IFileStorageService fileStorage)
    {
        _repository = repository;
        _customerRepository = customerRepository;
        _driverDisplayInfoProvider = driverDisplayInfoProvider;
        _fileStorage = fileStorage;
    }

    public async Task<Result<IReadOnlyList<BookingDto>>> Handle(GetMyBookingsQuery request, CancellationToken ct)
    {
        var customer = await _customerRepository.GetByEmailAsync(request.UserEmail, ct);
        if (customer == null)
            return Result.Ok<IReadOnlyList<BookingDto>>(new List<BookingDto>());

        var bookings = await _repository.GetByCustomerIdAsync(customer.Id, ct);
        var resolved = await BookingListProjector.ProjectAsync(bookings, _driverDisplayInfoProvider, _fileStorage, ct);
        return Result.Ok(resolved);
    }
}
