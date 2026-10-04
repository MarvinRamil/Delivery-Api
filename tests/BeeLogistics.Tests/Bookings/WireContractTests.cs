using System.Reflection;
using BeeLogistics.Modules.Bookings.Application.DTOs;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// These DTOs are positional records consumed by client apps, and their mappers construct them
/// positionally too. Appending a member is safe; <i>inserting</i> one shifts every subsequent
/// argument, and because adjacent members are often the same type the compiler stays quiet — the
/// failure surfaces as a booking whose notes are in the cargo field, on a phone, in production.
///
/// So this pins the trailing members. It is a deliberately annoying test: if it fails, either move
/// your new member to the end, or update the expectation on purpose.
/// </summary>
public class WireContractTests
{
    /// <summary>The primary constructor of a positional record, in declaration order.</summary>
    private static string[] PositionalParameterNames<T>()
    {
        var ctor = typeof(T).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        return ctor.GetParameters().Select(p => p.Name!).ToArray();
    }

    private static void AssertEndsWith<T>(params string[] expectedTail)
    {
        var actual = PositionalParameterNames<T>();
        var tail = actual.Skip(Math.Max(0, actual.Length - expectedTail.Length)).ToArray();
        Assert.Equal(expectedTail, tail);
    }

    [Fact]
    public void BookingDto_keeps_new_members_last()
    {
        // DeliveryMode was appended after the cancellation fields, not slotted next to ServiceType
        // where it reads more naturally, precisely because of this.
        AssertEndsWith<BookingDto>("CancellationReason", "CancelledBy", "CancelledAt", "DeliveryMode");
    }

    [Fact]
    public void CreateBookingDto_keeps_new_members_last()
    {
        AssertEndsWith<CreateBookingDto>("Notes", "PaymentMethod", "DeliveryMode");
    }

    [Fact]
    public void DriverBookingOfferWithDetailsDto_keeps_new_members_last()
    {
        AssertEndsWith<DriverBookingOfferWithDetailsDto>("Stops", "EarningDetails", "DeliveryMode");
    }

    [Fact]
    public void PricingResultDto_keeps_new_members_last()
    {
        AssertEndsWith<PricingResultDto>("DistanceKm", "Breakdown", "PoolingDiscount", "DeliveryMode");
    }

    [Fact]
    public void DeliveryStopDto_shape_is_unchanged()
    {
        AssertEndsWith<DeliveryStopDto>("ContactName", "ContactPhone", "Notes");
    }

    [Fact]
    public void CreateBookingDto_carries_the_mode_as_a_string_not_the_enum()
    {
        // The creation controller deserialises this DTO with its own JsonSerializerOptions, which
        // has no JsonStringEnumConverter. An enum-typed member fails to bind from "OnDemand" and
        // reads as Regular with no error — a confusing bug that this test makes impossible.
        var mode = typeof(CreateBookingDto).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First()
            .GetParameters()
            .Single(p => p.Name == "DeliveryMode");

        Assert.Equal(typeof(string), mode.ParameterType);
    }

    [Theory]
    [InlineData(typeof(BookingDto))]
    [InlineData(typeof(CreateBookingDto))]
    [InlineData(typeof(DriverBookingOfferWithDetailsDto))]
    [InlineData(typeof(PricingResultDto))]
    public void Client_facing_records_stay_records(Type type)
    {
        // A record's positional constructor is the contract. Converting one to a class with
        // settable properties would break every positional construction site at once.
        Assert.True(type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.NonPublic
            | BindingFlags.Public) is not null, $"{type.Name} is no longer a record");
    }
}
