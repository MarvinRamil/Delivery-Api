using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

// Queries
public record GetPendingOffersQuery(Guid DriverId, int Limit = 3) : IRequest<Result<IReadOnlyList<DriverBookingOfferWithDetailsDto>>>;

// Handlers
public class GetPendingOffersQueryHandler : IRequestHandler<GetPendingOffersQuery, Result<IReadOnlyList<DriverBookingOfferWithDetailsDto>>>
{
    private readonly IDriverBookingOfferRepository _offerRepository;
    private readonly IBookingRepository _bookingRepository;
    private readonly IPaymentRepository _paymentRepository;
    private readonly ILogger<GetPendingOffersQueryHandler> _logger;
    private readonly IFileStorageService _fileStorage;
    private readonly decimal _commissionRate;
    private readonly decimal? _cashChargeRate;

    public GetPendingOffersQueryHandler(
        IDriverBookingOfferRepository offerRepository,
        IBookingRepository bookingRepository,
        IPaymentRepository paymentRepository,
        IConfiguration configuration,
        ILogger<GetPendingOffersQueryHandler> logger,
        IFileStorageService fileStorage)
    {
        _offerRepository = offerRepository;
        _bookingRepository = bookingRepository;
        _paymentRepository = paymentRepository;
        _logger = logger;
        _fileStorage = fileStorage;
        // Read from the key DriverWalletOptions binds, so the quote and the later settlement
        // move together. This module cannot reference Drivers directly - Drivers references it.
        _commissionRate = configuration.GetValue(
            PlatformCommissionDefaults.RateConfigurationKey, PlatformCommissionDefaults.Rate);
        _cashChargeRate = configuration.GetValue<decimal?>(
            PlatformCommissionDefaults.CashChargeRateOverrideConfigurationKey);
    }

    public async Task<Result<IReadOnlyList<DriverBookingOfferWithDetailsDto>>> Handle(GetPendingOffersQuery request, CancellationToken ct)
    {
        _logger.LogInformation("[DriverOffers] GetPendingOffersQueryHandler: DriverId={DriverId}, Limit={Limit}", request.DriverId, request.Limit);

        // Get multiple pending offers (2-3, default 3)
        var limit = request.Limit < 1 ? 3 : request.Limit > 10 ? 10 : request.Limit; // Max 10, default 3
        var offers = await _offerRepository.GetPendingOffersForDriverAsync(request.DriverId, limit, ct);

        _logger.LogInformation("[DriverOffers] GetPendingOffersQueryHandler: Found {Count} raw pending offer(s) for driver {DriverId}", offers.Count, request.DriverId);

        if (!offers.Any())
        {
            _logger.LogInformation("[DriverOffers] GetPendingOffersQueryHandler: Returning empty list (no pending offers)");
            return Result.Ok<IReadOnlyList<DriverBookingOfferWithDetailsDto>>(new List<DriverBookingOfferWithDetailsDto>());
        }

        // One query for every offer's payment rather than one per offer - this endpoint is
        // polled by every idle driver.
        var paymentsByBooking = (await _paymentRepository.GetByBookingIdsAsync(
                offers.Select(o => o.BookingId).Distinct(), ct))
            .Where(p => p.BookingId.HasValue)
            .GroupBy(p => p.BookingId!.Value)
            .ToDictionary(g => g.Key, g => OfferEarningsCalculator.MethodFrom(g));

        // Fetch booking details with stops for each offer
        var result = new List<DriverBookingOfferWithDetailsDto>();

        foreach (var offer in offers)
        {
            var booking = await _bookingRepository.GetByIdAsync(offer.BookingId, ct);
            if (booking == null)
                continue; // Skip if booking not found

            // Only include active bookings that are not yet assigned to a driver
            // Check both Status and SelectedDriverId to ensure it's truly unassigned
            if (booking.SelectedDriverId.HasValue)
                continue; // Skip already assigned bookings
            
            // Only include bookings in active states (Pending, Confirmed, etc.)
            // Exclude cancelled and completed bookings
            if (booking.Status == BookingStatus.Cancelled || 
                booking.Status == BookingStatus.Completed)
                continue; // Skip inactive bookings

            // Map stops to DTOs
            var stopsDto = booking.Stops
                .OrderBy(s => s.Sequence)
                .Select(s => new DeliveryStopDto(
                    s.Id,
                    s.Sequence,
                    s.Address,
                    s.Type.ToString(), // "Pickup" or "Dropoff"
                    s.Status.ToString(),
                    s.ArrivedAt,
                    s.CompletedAt,
                    s.Latitude,
                    s.Longitude,
                    s.ContactName,
                    s.ContactPhone,
                    s.Notes
                ))
                .ToList();

            // Get customer name
            var customerName = "Unknown";
            if (booking.Customer != null && !string.IsNullOrWhiteSpace(booking.Customer.Name))
            {
                customerName = booking.Customer.Name;
            }
            else if (booking.Customer != null && !string.IsNullOrWhiteSpace(booking.Customer.Email))
            {
                customerName = booking.Customer.Email;
            }

            // Sanitize ItemImagePath
            string? sanitizedImagePath = null;
            if (!string.IsNullOrWhiteSpace(booking.ItemImagePath))
            {
                var path = booking.ItemImagePath;
                if (path.StartsWith("/") || path.StartsWith("http://") || path.StartsWith("https://"))
                {
                    sanitizedImagePath = path;
                }
                else if (path.Contains("\\") || (path.Length > 1 && path[1] == ':') || path.StartsWith("C:") || path.StartsWith("D:"))
                {
                    var fileName = System.IO.Path.GetFileName(path);
                    sanitizedImagePath = $"/uploads/{fileName}";
                }
                else
                {
                    sanitizedImagePath = path;
                }
            }

            var offerDto = new DriverBookingOfferWithDetailsDto(
                offer.Id,
                offer.BookingId,
                offer.DriverId,
                null, // Tenancy removed in #43; kept on the wire for older driver builds
                offer.Status,
                offer.OfferedAt,
                offer.RespondedAt,
                offer.ExpiresAt,
                offer.SequenceNumber,
                offer.DistanceKm,
                offer.IsFavouriteDriver,
                offer.DriverRating,
                offer.EstimatedArrivalMinutes,
                // Booking details
                booking.BookingNumber,
                booking.CustomerId,
                customerName,
                booking.VehicleType,
                booking.CargoDescription ?? string.Empty,
                booking.ScheduleDate,
                booking.Status,
                booking.Notes,
                booking.WeightKg,
                sanitizedImagePath,
                booking.ItemLengthCm,
                booking.ItemWidthCm,
                booking.ItemHeightCm,
                booking.EstimatedFare,
                booking.FinalFare,
                booking.DistanceKm,
                // Multi-stop information
                stopsDto,
                // What the driver takes home, so accept/decline is decided on the net (#58).
                // FinalFare is not set until completion, so at offer time this is an estimate.
                OfferEarningsCalculator.For(
                    booking.FinalFare ?? booking.EstimatedFare,
                    paymentsByBooking.GetValueOrDefault(offer.BookingId),
                    _commissionRate,
                    isEstimate: booking.FinalFare is null,
                    cashChargeRate: _cashChargeRate),
                booking.DeliveryMode
            );

            var resolved = await BookingImageUrlResolver.ResolveOfferAsync(offerDto, _fileStorage, ct);
            result.Add(resolved);
        }

        _logger.LogInformation("[DriverOffers] GetPendingOffersQueryHandler: Returning {Count} offer(s) for driver {DriverId}", result.Count, request.DriverId);
        return Result.Ok<IReadOnlyList<DriverBookingOfferWithDetailsDto>>(result);
    }
}
