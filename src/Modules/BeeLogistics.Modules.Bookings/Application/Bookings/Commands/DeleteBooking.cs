using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record DeleteBookingCommand(Guid Id, Guid CallerUserId, bool IsElevated) : IRequest<Result>;

public class DeleteBookingCommandHandler : IRequestHandler<DeleteBookingCommand, Result>
{
    private readonly IBookingRepository _repository;
    private readonly IBookingAccessPolicy _accessPolicy;

    public DeleteBookingCommandHandler(IBookingRepository repository, IBookingAccessPolicy accessPolicy)
    {
        _repository = repository;
        _accessPolicy = accessPolicy;
    }

    public async Task<Result> Handle(DeleteBookingCommand request, CancellationToken ct)
    {
        var booking = await _repository.GetByIdAsync(request.Id, ct);
        if (booking == null) return Result.NotFound("Booking not found");

        // Backstop: the endpoint is already gated by the Backoffice policy.
        if (!await _accessPolicy.CanAccessAsync(booking, request.CallerUserId, request.IsElevated, ct))
            return Result.Forbidden("You do not have access to this booking");

        // Soft delete rather than DbSet.Remove: the Booking query filter (!IsDeleted) hides it
        // from every read path, the row stays recoverable, and its FK-linked payment and
        // rating records survive.
        booking.SoftDelete(request.CallerUserId.ToString());
        await _repository.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
