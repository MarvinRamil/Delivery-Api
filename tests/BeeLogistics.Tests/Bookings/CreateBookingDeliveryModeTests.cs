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

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// The mode arrives as a string because the creation controller deserialises with its own
/// JsonSerializerOptions, which has no JsonStringEnumConverter — an enum-typed member there binds
/// to Regular silently instead of failing. These tests cover that string surviving the trip to a
/// persisted mode, and an unrecognised one being refused rather than quietly downgraded.
/// </summary>
public class CreateBookingDeliveryModeTests
{
    private readonly IBookingRepository _bookings = Substitute.For<IBookingRepository>();
    private readonly ICustomerRepository _customers = Substitute.For<ICustomerRepository>();
    private readonly IPricingService _pricing = Substitute.For<IPricingService>();
    private readonly IPublishEndpoint _publish = Substitute.For<IPublishEndpoint>();
    private readonly IPaymentRepository _payments = Substitute.For<IPaymentRepository>();
    private readonly IBookingEmailService _email = Substitute.For<IBookingEmailService>();
    private readonly IFileStorageService _files = Substitute.For<IFileStorageService>();

    public CreateBookingDeliveryModeTests()
    {
        _customers.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new Customer("Ana", "ana@example.com", "", "", ""));

        _bookings.FindDuplicateBookingAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<DateTime>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(),
            Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((Booking?)null);

        _pricing.CalculateFareAsync(Arg.Any<FareRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PricingResult { TotalFare = 150m, DistanceKm = 5m });
    }

    private CreateBookingCommandHandler Handler() => new(
        _bookings, _customers, _pricing, _publish, _payments, _email,
        new ConfigurationBuilder().Build(),
        NullLogger<CreateBookingCommandHandler>.Instance, _files);

    private static CreateBookingDto Dto(string? deliveryMode) => new(
        CustomerId: Guid.NewGuid(),
        VehicleType: "Motorcycle",
        CargoDescription: "Documents",
        ScheduleDate: DateTime.UtcNow.AddHours(2),
        ServiceType: "Immediate",
        Stops:
        [
            new DeliveryStopDto(null, 0, "Pickup St", "Pickup", Latitude: 16.61m, Longitude: 120.31m),
            new DeliveryStopDto(null, 1, "Dropoff Ave", "Dropoff", Latitude: 16.62m, Longitude: 120.33m),
        ],
        EstimatedFare: 150m,
        DeliveryMode: deliveryMode);

    private Booking? Added()
        => _bookings.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IBookingRepository.Add))
            .Select(c => c.GetArguments()[0] as Booking)
            .FirstOrDefault();

    [Theory]
    [InlineData("OnDemand", DeliveryMode.OnDemand)]
    [InlineData("onDEMAND", DeliveryMode.OnDemand)]
    [InlineData("Pooling", DeliveryMode.Pooling)]
    [InlineData("Regular", DeliveryMode.Regular)]
    public async Task The_mode_string_is_persisted_as_the_matching_mode(string sent, DeliveryMode expected)
    {
        var result = await Handler().Handle(new CreateBookingCommand(Dto(sent)), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, Added()!.DeliveryMode);
        Assert.Equal(DeliveryModeRanking.DispatchPriorityOf(expected), Added()!.DispatchPriority);
    }

    [Fact]
    public async Task Omitting_the_mode_yields_Regular()
    {
        // Every client that predates modes. Must not 400.
        var result = await Handler().Handle(new CreateBookingCommand(Dto(null)), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(DeliveryMode.Regular, Added()!.DeliveryMode);
    }

    [Fact]
    public async Task The_mode_is_echoed_back_on_the_response()
    {
        var result = await Handler().Handle(new CreateBookingCommand(Dto("Pooling")), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(DeliveryMode.Pooling, result.Value!.DeliveryMode);
    }

    [Theory]
    [InlineData("Express")]
    [InlineData("Priority")]  // the name this mode used to have
    [InlineData("7")]
    public async Task An_unrecognised_mode_is_refused_rather_than_downgraded(string sent)
    {
        // Silently falling back to Regular would charge a customer the wrong price for the wrong
        // service and give no indication anything went wrong.
        var result = await Handler().Handle(new CreateBookingCommand(Dto(sent)), default);

        Assert.True(result.IsFailure);
        Assert.Contains("Invalid delivery mode", result.Error!, StringComparison.Ordinal);
        Assert.Null(Added());
    }

    [Fact]
    public async Task The_mode_reaches_the_pricing_service()
    {
        // Replaces an earlier test asserting the mode did NOT change the fare — true while the
        // mode was only being persisted, and deliberately inverted here now that it prices.
        await Handler().Handle(new CreateBookingCommand(Dto("OnDemand")), default);

        await _pricing.Received(1).CalculateFareAsync(
            Arg.Is<FareRequest>(r => r.DeliveryMode == DeliveryMode.OnDemand),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_premium_the_pricing_service_returns_is_what_gets_persisted()
    {
        _pricing.CalculateFareAsync(Arg.Any<FareRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PricingResult { TotalFare = 155m, PriorityFee = 31m, DistanceKm = 8m });

        await Handler().Handle(new CreateBookingCommand(Dto("OnDemand")), default);

        Assert.Equal(155m, Added()!.EstimatedFare);
        Assert.Equal(31m, Added()!.PriorityFee);
    }

}
