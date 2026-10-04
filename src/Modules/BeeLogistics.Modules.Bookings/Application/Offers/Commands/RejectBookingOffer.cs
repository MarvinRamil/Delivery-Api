using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Infrastructure;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record RejectBookingOfferCommand(Guid OfferId, Guid DriverId) : IRequest<Result>;

public class RejectBookingOfferCommandHandler : IRequestHandler<RejectBookingOfferCommand, Result>
{
    private readonly IDriverBookingOfferRepository _offerRepository;
    private readonly IBookingRepository _bookingRepository;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<RejectBookingOfferCommandHandler> _logger;

    public RejectBookingOfferCommandHandler(
        IDriverBookingOfferRepository offerRepository,
        IBookingRepository bookingRepository,
        IPublishEndpoint publishEndpoint,
        ILogger<RejectBookingOfferCommandHandler> logger)
    {
        _offerRepository = offerRepository;
        _bookingRepository = bookingRepository;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task<Result> Handle(RejectBookingOfferCommand request, CancellationToken ct)
    {
        var offer = await _offerRepository.GetByIdAsync(request.OfferId, ct);
        if (offer == null)
            return Result.Fail("Offer not found");

        if (offer.DriverId != request.DriverId)
            return Result.Fail("Offer does not belong to this driver");

        if (offer.Status != DriverOfferStatus.Pending)
            return Result.Fail("Offer is not pending");

        offer.Reject();
        await _offerRepository.SaveChangesAsync(ct);

        // Tagged by mode because pooled offers pay the driver less and are therefore expected to be
        // rejected more often — an untagged spike reads as a dispatch bug. One projected
        // single-column query; the booking aggregate below is only loaded when offers run out.
        var modes = await _bookingRepository.GetDeliveryModesAsync(new[] { offer.BookingId }, ct);
        BeeMetrics.OffersRejected.Add(1, ModeTag(modes.GetValueOrDefault(offer.BookingId)));

        // Check if any pending offers remain for this booking
        var allOffers = await _offerRepository.GetByBookingIdAsync(offer.BookingId, ct);
        var hasPendingOffers = allOffers.Any(o => o.Status == DriverOfferStatus.Pending && !o.IsExpired);

        if (!hasPendingOffers)
        {
            var booking = await _bookingRepository.GetByIdAsync(offer.BookingId, ct);
            // Only re-broadcast bookings still waiting for a driver (not accepted/cancelled/completed).
            if (booking != null && booking.SelectedDriverId == null
                && booking.Status != BookingStatus.Cancelled
                && booking.Status != BookingStatus.Completed)
            {
                // Event-driven hand-off: instead of parking the booking and waiting for the
                // periodic pulse, immediately ask the broadcaster for the next wave of drivers
                // (the consumer's "already broadcasting" path offers only drivers not yet offered).
                _logger.LogInformation(
                    "[DriverOffers] All offers exhausted for booking {BookingId} after a reject; requesting next broadcast wave",
                    offer.BookingId);
                await _publishEndpoint.Publish(new BookingBroadcastRequested(offer.BookingId), ct);
                await _bookingRepository.SaveChangesAsync(ct);
                BeeMetrics.BookingsRejectedByAll.Add(1);
            }
        }

        return Result.Ok();
    }

    private static KeyValuePair<string, object?> ModeTag(DeliveryMode mode)
        => new("mode", mode.ToString());
}
