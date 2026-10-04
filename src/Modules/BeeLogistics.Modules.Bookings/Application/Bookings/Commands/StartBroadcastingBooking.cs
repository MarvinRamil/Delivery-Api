using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MediatR;
using Microsoft.Extensions.Logging;
using MassTransit;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record StartBroadcastingBookingCommand(Guid BookingId, Guid UserId) : IRequest<Result>;

public class StartBroadcastingBookingCommandHandler : IRequestHandler<StartBroadcastingBookingCommand, Result>
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<StartBroadcastingBookingCommandHandler> _logger;

    public StartBroadcastingBookingCommandHandler(
        IBookingRepository bookingRepository,
        IPublishEndpoint publishEndpoint,
        ILogger<StartBroadcastingBookingCommandHandler> logger)
    {
        _bookingRepository = bookingRepository;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task<Result> Handle(StartBroadcastingBookingCommand request, CancellationToken ct)
    {
        // Authorization is handled by controller's [Authorize(Policy = "Backoffice")]
        
        var booking = await _bookingRepository.GetByIdAsync(request.BookingId, ct);
        if (booking == null)
            return Result.Fail("Booking not found");

        // Do NOT set BroadcastingToDrivers here - the consumer sets it after creating offers.
        // If we set it here, the consumer sees "already being broadcasted" and skips creating offers.
        _logger.LogInformation("Publishing BookingBroadcastRequested for booking {BookingId}", request.BookingId);
        await _publishEndpoint.Publish(new BookingBroadcastRequested(request.BookingId), ct);
        // Outbox: message is only persisted when we SaveChanges on the same DbContext the outbox uses.
        await _bookingRepository.SaveChangesAsync(ct);
        _logger.LogInformation("BookingBroadcastRequested published and outbox saved for booking {BookingId}", request.BookingId);
        return Result.Ok();
    }
}
