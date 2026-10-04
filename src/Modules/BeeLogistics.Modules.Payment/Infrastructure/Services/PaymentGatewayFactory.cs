using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Options;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Payment.Infrastructure.Services;

public class PaymentGatewayFactory : IPaymentGatewayFactory
{
    private readonly IReadOnlyDictionary<string, IPaymentGateway> _gateways;
    private readonly string _activeGateway;

    public PaymentGatewayFactory(IEnumerable<IPaymentGateway> gateways, IOptions<PaymentGatewayOptions> options)
    {
        _gateways = gateways.ToDictionary(g => g.ProviderName, StringComparer.OrdinalIgnoreCase);
        _activeGateway = PaymentProviders.Normalize(options.Value.ActiveGateway)
            ?? throw new InvalidOperationException(
                $"Payments:ActiveGateway must be '{PaymentProviders.Xendit}' or '{PaymentProviders.PayMongo}', got '{options.Value.ActiveGateway}'.");
    }

    public IPaymentGateway GetActive() => Get(_activeGateway);

    public IPaymentGateway Get(string providerName)
    {
        var canonical = PaymentProviders.Normalize(providerName)
            ?? throw new InvalidOperationException($"Unknown payment provider '{providerName}'.");

        return _gateways.TryGetValue(canonical, out var gateway)
            ? gateway
            : throw new InvalidOperationException(
                $"Payment provider '{canonical}' is not registered. Configure its credentials to enable it.");
    }
}
