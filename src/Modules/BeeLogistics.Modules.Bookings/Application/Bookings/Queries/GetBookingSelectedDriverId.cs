using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Contracts;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

/// <summary>
/// Handles <see cref="GetBookingSelectedDriverIdQuery"/> (defined in Shared.Contracts so the
/// Payment module can send it without a project reference to Bookings) - a cross-module read of
/// which driver, if any, is assigned to a booking.
/// </summary>
public class GetBookingSelectedDriverIdQueryHandler : IRequestHandler<GetBookingSelectedDriverIdQuery, Guid?>
{
    private readonly IBookingRepository _repository;

    public GetBookingSelectedDriverIdQueryHandler(IBookingRepository repository)
    {
        _repository = repository;
    }

    public async Task<Guid?> Handle(GetBookingSelectedDriverIdQuery request, CancellationToken ct)
    {
        var booking = await _repository.GetByIdAsync(request.BookingId, ct);
        return booking?.SelectedDriverId;
    }
}
