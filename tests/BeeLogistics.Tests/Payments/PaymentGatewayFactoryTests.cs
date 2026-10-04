using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Options;
using BeeLogistics.Modules.Payment.Infrastructure.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Payments;

public class PaymentGatewayFactoryTests
{
    private static IPaymentGateway Gateway(string name)
    {
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.ProviderName.Returns(name);
        return gateway;
    }

    private static PaymentGatewayFactory Factory(string activeGateway, params IPaymentGateway[] gateways)
        => new(gateways, Options.Create(new PaymentGatewayOptions { ActiveGateway = activeGateway }));

    [Fact]
    public void GetActive_returns_configured_active_gateway()
    {
        var xendit = Gateway(PaymentProviders.Xendit);
        var paymongo = Gateway(PaymentProviders.PayMongo);

        var factory = Factory("paymongo", xendit, paymongo);

        Assert.Same(paymongo, factory.GetActive());
    }

    [Theory]
    [InlineData("PayMongo")]
    [InlineData("PAYMONGO")]
    [InlineData("paymongo")]
    public void ActiveGateway_is_case_insensitive(string configured)
    {
        var paymongo = Gateway(PaymentProviders.PayMongo);
        var factory = Factory(configured, Gateway(PaymentProviders.Xendit), paymongo);

        Assert.Same(paymongo, factory.GetActive());
    }

    [Fact]
    public void Get_resolves_by_stored_provider_regardless_of_active()
    {
        var xendit = Gateway(PaymentProviders.Xendit);
        var factory = Factory("paymongo", xendit, Gateway(PaymentProviders.PayMongo));

        Assert.Same(xendit, factory.Get("xendit"));
    }

    [Fact]
    public void Invalid_active_gateway_throws_at_construction()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Factory("stripe", Gateway(PaymentProviders.Xendit)));
    }

    [Fact]
    public void Get_unknown_provider_throws()
    {
        var factory = Factory("xendit", Gateway(PaymentProviders.Xendit));
        Assert.Throws<InvalidOperationException>(() => factory.Get("stripe"));
    }

    [Fact]
    public void Get_known_but_unregistered_provider_throws_with_configuration_hint()
    {
        var factory = Factory("xendit", Gateway(PaymentProviders.Xendit));
        var ex = Assert.Throws<InvalidOperationException>(() => factory.Get("paymongo"));
        Assert.Contains("not registered", ex.Message);
    }
}

public class BankCodeMapParityTests
{
    [Fact]
    public void Every_xendit_bank_code_has_a_paymongo_equivalent()
    {
        // Saved withdrawal methods store the friendly code (BPI, BDO...); after the
        // gateway switch those records must still resolve to a PayMongo BIC.
        foreach (var code in XenditBankCodeMap.KnownCodes)
        {
            var bic = PayMongoBankCodeMap.Normalize(code);
            Assert.False(string.IsNullOrWhiteSpace(bic), $"No PayMongo mapping for '{code}'");
        }
    }

    [Fact]
    public void Xendit_channel_codes_resolve_through_paymongo_map()
    {
        // Legacy records that stored the Xendit channel code directly
        // Prefix, not equality: PayMongo returns the full 11-character BIC (BOPIPHMMXXX);
        // the first eight identify the institution.
        Assert.StartsWith("BOPIPHMM", PayMongoBankCodeMap.Normalize("PH_BPI"));
    }

    [Fact]
    public void Unknown_bank_code_throws_for_paymongo()
    {
        // A wrong BIC would send money to the wrong institution — fail loudly.
        Assert.Throws<ArgumentException>(() => PayMongoBankCodeMap.Normalize("NOT A BANK"));
    }
}
