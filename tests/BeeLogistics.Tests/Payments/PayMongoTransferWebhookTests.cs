using BeeLogistics.Modules.Payment.Presentation.Controllers;
using Xunit;

namespace BeeLogistics.Tests.Payments;

public class PayMongoTransferWebhookTests
{
    [Theory]
    // The events the account actually subscribes to. Their names carry the outcome, so a
    // payload without attributes.status must still resolve — dropping it would leave the
    // withdrawal stuck in Approved with the driver's payout held.
    [InlineData("transfer.outward.successful", "SUCCEEDED")]
    [InlineData("transfer.outward.failed", "FAILED")]
    [InlineData("payout.returned", "FAILED")]
    [InlineData("TRANSFER.OUTWARD.SUCCESSFUL", "SUCCEEDED")]
    public void Event_name_implies_the_outcome(string eventType, string expected)
        => Assert.Equal(expected, PayMongoWebhooksController.StatusFromEventName(eventType));

    [Theory]
    // Non-terminal or unrelated events stay a no-op rather than being forced into a status.
    [InlineData("transfer.outward.pending")]
    [InlineData("transfer.created")]
    [InlineData("checkout_session.payment.paid")]
    public void Non_terminal_events_imply_nothing(string eventType)
        => Assert.Null(PayMongoWebhooksController.StatusFromEventName(eventType));
}
