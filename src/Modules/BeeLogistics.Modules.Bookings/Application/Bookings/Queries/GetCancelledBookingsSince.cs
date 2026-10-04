using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

/// <summary>
/// Answers <see cref="GetCancelledBookingsSinceQuery"/> for the Payment module's reconciliation
/// sweep (GitLab #51). Payment has its own DbContext and schema, so it cannot join on booking
/// status; Bookings owns that table and is the only module that can answer this. Same
/// Shared.Contracts + MediatR route as <c>GetActiveVehicleTypesQuery</c>.
/// </summary>
/// <remarks>
/// Returns cancellation metadata only, never the aggregate: what a booking contains stays inside
/// this module. The caller needs just enough to look up a payment and decide whose fault the
/// cancellation was.
/// </remarks>
public class GetCancelledBookingsSinceQueryHandler
    : IRequestHandler<GetCancelledBookingsSinceQuery, Result<IReadOnlyList<CancelledBookingInfo>>>
{
    private readonly IBookingRepository _repository;

    public GetCancelledBookingsSinceQueryHandler(IBookingRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<CancelledBookingInfo>>> Handle(
        GetCancelledBookingsSinceQuery request, CancellationToken ct)
    {
        var cancelled = await _repository.GetCancelledSinceAsync(request.SinceUtc, request.Limit, ct);
        return Result.Ok(cancelled);
    }
}
