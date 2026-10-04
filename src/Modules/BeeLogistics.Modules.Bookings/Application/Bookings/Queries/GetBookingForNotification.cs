using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

// Cross-module notification handlers
/// <summary>
/// Handler for getting minimal booking info for notifications.
/// Used by Operations module when sending customer notifications.
/// </summary>
public class GetBookingForNotificationQueryHandler : IRequestHandler<GetBookingForNotificationQuery, Result<BookingNotificationInfo>>
{
    private readonly IBookingRepository _repository;
    
    public GetBookingForNotificationQueryHandler(IBookingRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<BookingNotificationInfo>> Handle(GetBookingForNotificationQuery request, CancellationToken ct)
    {
        var booking = await _repository.GetByIdAsync(request.BookingId, ct);
        if (booking == null) return Result.NotFound<BookingNotificationInfo>("Booking not found");
        
        return Result.Ok(new BookingNotificationInfo(booking.Id, booking.BookingNumber, booking.CustomerId));
    }
}
