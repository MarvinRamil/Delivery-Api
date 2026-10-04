using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Bookings.Queries;

/// <summary>
/// Answers the Messaging module's backstop sweep: which bookings exist since a cutoff.
/// </summary>
/// <remarks>
/// Deliberately returns a minimal projection rather than <c>Booking</c> — this crosses a module
/// boundary and feeds a batch sweep, so handing over the full aggregate would leak the Bookings
/// domain into Messaging for no benefit. Same shape as <c>GetCancelledBookingsSinceQuery</c>.
/// </remarks>
public class GetBookingsCreatedSinceQueryHandler
    : IRequestHandler<GetBookingsCreatedSinceQuery, Result<IReadOnlyList<BookingForChatRoom>>>
{
    private readonly IBookingRepository _repository;

    public GetBookingsCreatedSinceQueryHandler(IBookingRepository repository) => _repository = repository;

    public async Task<Result<IReadOnlyList<BookingForChatRoom>>> Handle(
        GetBookingsCreatedSinceQuery request, CancellationToken ct)
    {
        var bookings = await _repository.GetCreatedSinceAsync(request.SinceUtc, request.Limit, ct);

        IReadOnlyList<BookingForChatRoom> result = bookings
            .Select(b => new BookingForChatRoom(
                b.Id, b.CustomerId, b.BookingNumber, b.PickupLocation, b.DropoffLocation, b.CreatedAt))
            .ToList();

        return Result.Ok(result);
    }
}
