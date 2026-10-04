using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Notification.Application.Services;
using BeeLogistics.Shared.Abstractions;
using PaymentEntity = BeeLogistics.Modules.Payment.Domain.Payment;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Infrastructure;
using MassTransit;
using Microsoft.Extensions.Configuration;
using MediatR;
using Microsoft.Extensions.Logging;

// ICustomerIdentityResolver: resolves Bookings Customer.Id → Identity UserId for SignalR routing

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

/// <summary>
/// The one booking-creation command. Both the JSON and multipart forms of
/// <c>POST /api/bookings</c> land here.
/// </summary>
/// <remarks>
/// Consolidated from two commands that had drifted apart: a flat pickup/dropoff one that never
/// set a fare at all, and a stops-based one no client ever called. Idempotency and the
/// confirmation email came from the former; service type, payment method and pricing from the
/// latter.
/// </remarks>
public record CreateBookingCommand(
    CreateBookingDto Dto,
    string? UserEmail = null,
    string? UserFullName = null
) : IRequest<Result<BookingDto>>;

public class CreateBookingCommandHandler : IRequestHandler<CreateBookingCommand, Result<BookingDto>>
{
    /// <summary>
    /// How far back to look for an identical booking from the same customer. Guards against a
    /// double-tap or a retried request creating two real deliveries.
    /// </summary>
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(5);

    private readonly IBookingRepository _repository;
    private readonly ICustomerRepository _customerRepository;
    private readonly IPricingService _pricingService;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly IPaymentRepository _paymentRepository;
    private readonly IBookingEmailService _bookingEmailService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CreateBookingCommandHandler> _logger;
    private readonly IFileStorageService _fileStorage;

    public CreateBookingCommandHandler(
        IBookingRepository repository,
        ICustomerRepository customerRepository,
        IPricingService pricingService,
        IPublishEndpoint publishEndpoint,
        IPaymentRepository paymentRepository,
        IBookingEmailService bookingEmailService,
        IConfiguration configuration,
        ILogger<CreateBookingCommandHandler> logger,
        IFileStorageService fileStorage)
    {
        _repository = repository;
        _customerRepository = customerRepository;
        _pricingService = pricingService;
        _publishEndpoint = publishEndpoint;
        _paymentRepository = paymentRepository;
        _bookingEmailService = bookingEmailService;
        _configuration = configuration;
        _logger = logger;
        _fileStorage = fileStorage;
    }

    public async Task<Result<BookingDto>> Handle(CreateBookingCommand request, CancellationToken ct)
    {
        var dto = request.Dto;

        // A booking is exactly one pickup and one dropoff. Multiple deliveries are multiple
        // bookings; a driver may hold up to DriverCapacityPolicy.DefaultMaxActive of them.
        if (dto.Stops == null || dto.Stops.Count != 2)
            return Result.Fail<BookingDto>("Exactly two stops are required: one pickup and one dropoff");

        var pickupStops = dto.Stops.Where(s => s.Type.Equals("Pickup", StringComparison.OrdinalIgnoreCase)).ToList();
        if (pickupStops.Count != 1)
            return Result.Fail<BookingDto>("Exactly one pickup stop is required");

        var dropoffStops = dto.Stops.Where(s => s.Type.Equals("Dropoff", StringComparison.OrdinalIgnoreCase)).ToList();
        if (dropoffStops.Count != 1)
            return Result.Fail<BookingDto>("Exactly one dropoff stop is required");

        // Get or create customer
        Guid customerId;
        if (!string.IsNullOrEmpty(request.UserEmail))
        {
            var customer = await _customerRepository.GetByEmailAsync(request.UserEmail, ct);
            if (customer == null)
            {
                var customerName = !string.IsNullOrEmpty(request.UserFullName)
                    ? request.UserFullName
                    : request.UserEmail.Split('@')[0];

                customer = new Customer(
                    name: customerName,
                    email: request.UserEmail,
                    companyName: string.Empty,
                    phone: string.Empty,
                    address: string.Empty
                );
                _customerRepository.Add(customer);
                await _customerRepository.SaveChangesAsync(ct);
            }
            customerId = customer.Id;
        }
        else
        {
            customerId = dto.CustomerId;
            var customer = await _customerRepository.GetByIdAsync(customerId, ct);
            if (customer == null)
                return Result.Fail<BookingDto>("Customer not found");
        }

        // Parse service type
        if (!Enum.TryParse<ServiceType>(dto.ServiceType, ignoreCase: true, out var serviceType))
            return Result.Fail<BookingDto>($"Invalid service type: {dto.ServiceType}");

        // Parse the delivery mode alongside it. Absent means Regular, so senders that know nothing
        // about modes keep working. Unlike the ServiceType parse above, this rejects numeric
        // strings rather than accepting an undefined enum value.
        if (!DeliveryModePolicy.TryParse(dto.DeliveryMode, out var deliveryMode))
            return Result.Fail<BookingDto>($"Invalid delivery mode: {dto.DeliveryMode}");

        // Idempotency: a double-tap or a retried request must not create two real deliveries.
        // Carried over from the flat-form command, which was the only path that had it. Matched on
        // the flat pickup/dropoff columns, which Booking back-fills from the stops.
        var pickup = pickupStops[0];
        var dropoff = dropoffStops[0];
        var existing = await _repository.FindDuplicateBookingAsync(
            customerId,
            pickup.Address,
            dropoff.Address,
            dto.VehicleType,
            dto.ScheduleDate,
            dto.WeightKg,
            pickup.Latitude,
            pickup.Longitude,
            dropoff.Latitude,
            dropoff.Longitude,
            DateTime.UtcNow - DuplicateWindow,
            ct);

        if (existing != null)
        {
            _logger.LogInformation(
                "[BookingCreate] Duplicate within {Minutes}m for customer {CustomerId}; returning booking {BookingId}",
                DuplicateWindow.TotalMinutes, customerId, existing.Id);
            return Result.Ok(await BookingImageUrlResolver.ResolveAsync(
                BookingMapper.ToDto(existing), _fileStorage, ct));
        }

        // Create stops first (EF Core will set BookingId when booking is saved via navigation property)
        var stops = dto.Stops.Select((s, index) => new DeliveryStop(
            Guid.Empty, // Temporary - EF Core will set this automatically when booking is saved
            index,
            s.Address,
            s.Type.Equals("Pickup", StringComparison.OrdinalIgnoreCase) ? StopType.Pickup : StopType.Dropoff,
            s.Latitude,
            s.Longitude,
            s.ContactName,
            s.ContactPhone,
            s.Notes
        )).ToList();

        // FARE TRUST: the fare is recomputed here and the client's EstimatedFare/PriorityFee are
        // never trusted. Before this, a customer could POST estimatedFare: 1 and a driver would be
        // offered — and for Cash, collect — one peso.
        //
        // Placed after the customer resolve/save and before Add and any Publish, so a pricing
        // failure or a requote leaves at most a Customer row and never a half-published outbox.
        PricingResult pricing;
        try
        {
            pricing = await _pricingService.CalculateFareAsync(
                new FareRequest(dto.VehicleType, stops, dto.WeightKg, dto.ScheduledDateTime, deliveryMode), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BookingCreate] Could not price booking for customer {CustomerId}", customerId);
            return Result.Fail<BookingDto>("We could not price this booking right now. Please try again.");
        }

        var enforce = FareTrust.IsEnforced(_configuration);
        var tolerance = FareTrust.Tolerance(pricing.TotalFare, _configuration);
        var decision = FareTrust.Decide(dto.EstimatedFare, pricing.TotalFare, tolerance, enforce);

        BeeMetrics.FareRecomputeDrift.Record(
            (double)(pricing.TotalFare - dto.EstimatedFare),
            new KeyValuePair<string, object?>("outcome",
                decision == FareTrustDecision.RequoteRequired ? "requote" : "accepted"));

        if (decision == FareTrustDecision.RequoteRequired)
        {
            _logger.LogInformation(
                "[BookingCreate] Requote required for customer {CustomerId}: quoted {Quoted}, server {Server}",
                customerId, dto.EstimatedFare, pricing.TotalFare);
            return Result.Fail<BookingDto>(
                $"The fare has changed since your quote (now ₱{pricing.TotalFare:F2}). Please review the price and try again.");
        }

        if (Math.Abs(pricing.TotalFare - dto.EstimatedFare) > tolerance)
        {
            // Logged even when not enforcing — this is the signal that decides whether the 400
            // above can safely be switched on.
            _logger.LogWarning(
                "[BookingCreate] Fare drift beyond tolerance for customer {CustomerId}: quoted {Quoted}, server {Server}, tolerance {Tolerance}, enforce {Enforce}",
                customerId, dto.EstimatedFare, pricing.TotalFare, tolerance, enforce);
        }

        // Create booking with stops
        // EF Core will automatically set BookingId on stops when booking is saved due to the navigation property relationship
        var booking = new Booking(
            customerId,
            dto.VehicleType,
            dto.CargoDescription,
            dto.ScheduleDate,
            serviceType,
            pricing.TotalFare,
            stops,
            dto.WeightKg,
            pricing.PriorityFee,
            dto.ScheduledDateTime,
            dto.ScheduledPickupWindow,
            dto.FavouriteDriverId,
            dto.ItemImagePath,
            dto.ItemLengthCm,
            dto.ItemWidthCm,
            dto.ItemHeightCm,
            dto.Notes,
            deliveryMode
        );

        // DistanceKm was never set at creation, so DriverBookingOfferWithDetailsDto.DistanceKmTotal
        // was always null for the driver. We have just computed it; keep it.
        booking.SetDistance(pricing.DistanceKm);

        _repository.Add(booking);
        // Persist the booking and the OrderCreated event first (outbox message commits in this transaction).
        // The broadcast is deliberately published later, AFTER the cash payment exists (see below).
        await _publishEndpoint.Publish(new OrderCreatedEvent { BookingId = booking.Id, CustomerId = customerId, CreatedAt = DateTime.UtcNow }, ct);
        // Chat room provisioning (#78). Published here so it commits in the same transaction as the
        // booking: a booking that fails to persist must not leave an orphan room behind, and a
        // booking that does persist must always get its room even if the broker is down right now.
        // The room starts as customer + bot; the driver joins on acceptance, since none exists yet.
        await _publishEndpoint.Publish(new BookingChatRoomRequested
        {
            BookingId = booking.Id,
            CustomerId = customerId,
            BookingNumber = booking.BookingNumber,
            PickupLocation = booking.PickupLocation,
            DropoffLocation = booking.DropoffLocation,
            CreatedAt = DateTime.UtcNow,
        }, ct);
        await _repository.SaveChangesAsync(ct); // EF Core will set BookingId on stops; outbox entry committed here too
        _logger.LogInformation("[BookingCreate] Booking {BookingId} saved (multi-stop).", booking.Id);

        // Counted after the save, so a booking that failed to persist is not counted as adoption.
        // This is the denominator for every other per-mode metric.
        BeeMetrics.BookingsCreatedByMode.Add(1, new KeyValuePair<string, object?>("mode", deliveryMode.ToString()));

        // When customer chooses Cash, create a cash-on-delivery payment so EarningCreditConsumer and
        // CashDeliverySettlementConsumer run on booking completion. Create it BEFORE broadcasting so
        // we never offer drivers a booking that has no payment record. If this fails, the booking
        // stays in PendingAssignment and the safety-net pulse re-broadcasts it once the issue clears.
        var paymentMethod = dto.PaymentMethod?.Trim();
        if (!string.IsNullOrEmpty(paymentMethod) && string.Equals(paymentMethod, "Cash", StringComparison.OrdinalIgnoreCase))
        {
            // booking.EstimatedFare, not dto.EstimatedFare: this is the amount the driver
            // physically collects from the customer, so a client-supplied number here was money.
            var cashPayment = PaymentEntity.CreateCashOnDelivery(booking.Id, customerId, booking.EstimatedFare);
            _paymentRepository.Add(cashPayment);
            await _paymentRepository.SaveChangesAsync(ct);
            _logger.LogInformation("[BookingCreate] Created cash-on-delivery payment for booking {BookingId}", booking.Id);
        }

        // Now broadcast to drivers. Publish then SaveChanges so the outbox message commits.
        _logger.LogInformation("[BookingCreate] Publishing BookingBroadcastRequested for booking {BookingId} (multi-stop)", booking.Id);
        await _publishEndpoint.Publish(new BookingBroadcastRequested(booking.Id), ct);
        await _repository.SaveChangesAsync(ct);
        _logger.LogInformation("[BookingCreate] Broadcast outbox committed for booking {BookingId}. Will be sent to RabbitMQ by the outbox dispatcher.", booking.Id);

        // Confirmation email, carried over from the flat-form command. Awaited so the
        // NotificationDbContext is not disposed before the message is queued. Never fails the
        // booking: the delivery is already committed and broadcasting by this point.
        if (!string.IsNullOrEmpty(request.UserEmail))
        {
            try
            {
                await _bookingEmailService.SendBookingConfirmationAsync(
                    request.UserEmail,
                    request.UserFullName ?? "Customer",
                    booking.BookingNumber,
                    booking.PickupLocation,
                    booking.DropoffLocation,
                    booking.ScheduleDate,
                    booking.VehicleType,
                    booking.CargoDescription,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[BookingCreate] Confirmation email failed for booking {BookingId}", booking.Id);
            }
        }

        var resultDto = await BookingImageUrlResolver.ResolveAsync(BookingMapper.ToDto(booking), _fileStorage, ct);
        return Result.Ok(resultDto);
    }
}
