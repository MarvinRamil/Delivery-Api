using BeeLogistics.Modules.Payment.Application.Banks;

namespace BeeLogistics.Modules.Drivers.Application.Services;

/// <summary>
/// Picks the rail a withdrawal should travel on, and the BIC that goes with it.
///
/// <para>
/// PESONet matters less for its ₱10,000,000 ceiling than for coverage: some institutions in the
/// catalog support only one of the two rails, so offering both widens where a driver can cash out.
/// </para>
/// <para>
/// It never silently downgrades InstaPay to PESONet. A driver who asked for an instant transfer and
/// gets their money on the next banking day files a support ticket, so an amount that InstaPay
/// cannot carry is rejected with the reason named instead — the behaviour
/// <c>PayMongoGateway</c>'s old silent switch got wrong.
/// </para>
/// </summary>
public static class WithdrawalRailSelector
{
    public sealed record Selection(TransferRail Rail, string Bic, bool IsInstant)
    {
        public string ArrivalDescription => IsInstant ? "Instant" : "Arrives next banking day";
    }

    public sealed record Rejection(string Reason);

    /// <summary>
    /// Returns the rail to use, or a rejection naming why the institution cannot carry the amount.
    /// </summary>
    public static (Selection? Selection, Rejection? Rejection) Select(PhBank bank, decimal amount)
    {
        if (amount <= 0)
            return (null, new Rejection("Enter an amount greater than zero."));

        var instapayFits = bank.Supports(TransferRail.Instapay) && amount <= PhBank.InstapayLimit;
        if (instapayFits)
        {
            var bic = bank.BicFor(TransferRail.Instapay);
            if (!string.IsNullOrWhiteSpace(bic))
                return (new Selection(TransferRail.Instapay, bic, IsInstant: true), null);
        }

        if (bank.Supports(TransferRail.Pesonet) && amount <= PhBank.PesonetLimit)
        {
            var bic = bank.BicFor(TransferRail.Pesonet);
            if (!string.IsNullOrWhiteSpace(bic))
                return (new Selection(TransferRail.Pesonet, bic, IsInstant: false), null);
        }

        // Nothing can carry it. Say which limit was hit rather than "unavailable" - the driver can
        // act on "split it up", and cannot act on a shrug.
        if (bank.Supports(TransferRail.Instapay) && !bank.Supports(TransferRail.Pesonet))
            return (null, new Rejection(
                $"{bank.Name} accepts up to ₱{PhBank.InstapayLimit:N0} per transfer. "
                + "Withdraw a smaller amount, or choose another bank."));

        if (amount > PhBank.PesonetLimit)
            return (null, new Rejection($"The most you can withdraw at once is ₱{PhBank.PesonetLimit:N0}."));

        return (null, new Rejection($"{bank.Name} is not available for withdrawals right now."));
    }
}
