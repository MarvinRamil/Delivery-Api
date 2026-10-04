namespace BeeLogistics.Modules.Payment.Application.Gateways;

public interface IPaymentGatewayFactory
{
    /// <summary>
    /// Gateway for NEW payments/top-ups/withdrawals/saved methods, selected by
    /// Payments:ActiveGateway. Never use this for records that already exist.
    /// </summary>
    IPaymentGateway GetActive();

    /// <summary>
    /// Gateway for an EXISTING record, resolved from the Provider stored on it.
    /// Keeps refunds/reconciliation working for in-flight records after the
    /// active gateway is switched. Throws for unknown/unregistered providers.
    /// </summary>
    IPaymentGateway Get(string providerName);
}
