using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Handlers;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Notification.Application.Services;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using PaymentEntity = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// The create handler used to inject IPricingService and never call it: it persisted the client's
/// EstimatedFare verbatim and built the cash-on-delivery payment from the same number. A booking
/// posted with <c>estimatedFare: 1</c> quoted the driver ~₱1 and, for cash, was ₱1 they actually
/// collected from the customer.
///
/// These tests pin that the server's number is the one that reaches the database, the driver, and
/// the cash float — and that a rejected booking leaves nothing behind.
/// </summary>
public class CreateBookingFareTrustTests
{
    private const decimal ServerFare = 312.50m;

    private readonly IBookingRepository _bookings = Substitute.For<IBookingRepository>();
    private readonly ICustomerRepository _customers = Substitute.For<ICustomerRepository>();
    private readonly IPricingService _pricing = Substitute.For<IPricingService>();
    private readonly IPublishEndpoint _publish = Substitute.For<IPublishEndpoint>();
    private readonly IPaymentRepository _payments = Substitute.For<IPaymentRepository>();
    private readonly IBookingEmailService _email = Substitute.For<IBookingEmailService>();
    private readonly IFileStorageService _files = Substitute.For<IFileStorageService>();

    private readonly Guid _customerId = Guid.NewGuid();

    public CreateBookingFareTrustTests()
    {
        _customers.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new Customer("Ana", "ana@example.com", "", "", ""));

        // No prior identical booking unless a test says otherwise.
        _bookings.FindDuplicateBookingAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<DateTime>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(),
            Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((Booking?)null);

        _pricing.CalculateFareAsync(Arg.Any<FareRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PricingResult
            {
                BaseFare = 50m,
                DistanceFare = 262.50m,
                TotalFare = ServerFare,
                DistanceKm = 27.5m,
                PriorityFee = 0m,
            });
    }

    private CreateBookingCommandHandler Handler(params (string Key, string? Value)[] settings)
        => new(_bookings, _customers, _pricing, _publish, _payments, _email,
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
                .Build(),
            NullLogger<CreateBookingCommandHandler>.Instance, _files);

    private CreateBookingDto Dto(decimal estimatedFare, decimal? priorityFee = null, string? paymentMethod = null)
        => new(
            CustomerId: _customerId,
            VehicleType: "Motorcycle",
            CargoDescription: "Documents",
            ScheduleDate: DateTime.UtcNow.AddHours(2),
            ServiceType: "Immediate",
            Stops:
            [
                new DeliveryStopDto(null, 0, "Pickup St", "Pickup", Latitude: 16.61m, Longitude: 120.31m),
                new DeliveryStopDto(null, 1, "Dropoff Ave", "Dropoff", Latitude: 16.62m, Longitude: 120.33m),
            ],
            EstimatedFare: estimatedFare,
            PriorityFee: priorityFee,
            PaymentMethod: paymentMethod);

    private Booking? AddedBooking()
        => _bookings.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IBookingRepository.Add))
            .Select(c => c.GetArguments()[0] as Booking)
            .FirstOrDefault();

    [Fact]
    public async Task The_persisted_fare_is_the_servers_not_the_clients()
    {
        var result = await Handler().Handle(new CreateBookingCommand(Dto(estimatedFare: 1m), "ana@example.com"), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(ServerFare, AddedBooking()!.EstimatedFare);
    }

    [Fact]
    public async Task A_client_supplied_priority_fee_is_ignored()
    {
        await Handler().Handle(new CreateBookingCommand(Dto(1m, priorityFee: 9999m), "ana@example.com"), default);

        Assert.Equal(0m, AddedBooking()!.PriorityFee);
    }

    [Fact]
    public async Task The_cash_payment_is_created_from_the_server_fare()
    {
        // The money bug. This amount is what the driver physically collects, so a client-supplied
        // number here was not a display bug — it was cash.
        await Handler().Handle(
            new CreateBookingCommand(Dto(1m, paymentMethod: "Cash"), "ana@example.com"), default);

        var payment = _payments.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IPaymentRepository.Add))
            .Select(c => c.GetArguments()[0] as PaymentEntity)
            .FirstOrDefault();

        Assert.NotNull(payment);
        Assert.Equal(ServerFare, payment!.Amount);
    }

    [Fact]
    public async Task Distance_is_recorded_so_the_driver_sees_it_on_the_offer()
    {
        // DistanceKm was never set at creation, so DriverBookingOfferWithDetailsDto.DistanceKmTotal
        // was always null. We compute it for the fare anyway.
        await Handler().Handle(new CreateBookingCommand(Dto(ServerFare), "ana@example.com"), default);

        Assert.Equal(27.5m, AddedBooking()!.DistanceKm);
    }

    [Fact]
    public async Task A_requote_creates_nothing_and_publishes_nothing()
    {
        var result = await Handler(("Pricing:FareTrust:Enforce", "true"))
            .Handle(new CreateBookingCommand(Dto(estimatedFare: 1m), "ana@example.com"), default);

        Assert.True(result.IsFailure);
        Assert.Contains("fare has changed", result.Error!, StringComparison.OrdinalIgnoreCase);

        // The check runs before Add and before any Publish, so a refused booking leaves no row
        // and no half-committed outbox message.
        Assert.Null(AddedBooking());
        await _publish.DidNotReceive().Publish(Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_honest_quote_is_accepted_while_enforcement_is_on()
    {
        var result = await Handler(("Pricing:FareTrust:Enforce", "true"))
            .Handle(new CreateBookingCommand(Dto(ServerFare)), default);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Nothing_is_refused_while_enforcement_is_off()
    {
        // Default posture: the fare hole is closed from day one, but no customer sees a 400 until
        // the drift histogram says it is safe.
        var result = await Handler().Handle(new CreateBookingCommand(Dto(estimatedFare: 1m)), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(ServerFare, AddedBooking()!.EstimatedFare);
    }

    [Fact]
    public async Task A_pricing_failure_refuses_the_booking_rather_than_saving_an_unpriced_one()
    {
        _pricing.CalculateFareAsync(Arg.Any<FareRequest>(), Arg.Any<CancellationToken>())
            .Returns<PricingResult>(_ => throw new InvalidOperationException("pricing down"));

        var result = await Handler().Handle(new CreateBookingCommand(Dto(300m)), default);

        Assert.True(result.IsFailure);
        Assert.Null(AddedBooking());
    }

    [Fact]
    public async Task More_than_two_stops_is_refused_before_anything_is_priced()
    {
        var dto = Dto(300m) with
        {
            Stops =
            [
                new DeliveryStopDto(null, 0, "Pickup", "Pickup", Latitude: 16.61m, Longitude: 120.31m),
                new DeliveryStopDto(null, 1, "Drop A", "Dropoff", Latitude: 16.62m, Longitude: 120.33m),
                new DeliveryStopDto(null, 2, "Drop B", "Dropoff", Latitude: 16.63m, Longitude: 120.34m),
            ],
        };

        var result = await Handler().Handle(new CreateBookingCommand(dto), default);

        Assert.True(result.IsFailure);
        await _pricing.DidNotReceive().CalculateFareAsync(
            Arg.Any<FareRequest>(), Arg.Any<CancellationToken>());
    }
}
