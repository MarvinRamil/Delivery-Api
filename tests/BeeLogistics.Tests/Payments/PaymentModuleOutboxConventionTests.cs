using System.Reflection;
using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Modules.Payment.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// Guards the publishing convention for the Payment module. GitLab #65 **inverted** it, so the
/// history matters more than usual here.
///
/// Originally the MassTransit EF outbox existed only on BookingsDbContext. With UseBusOutbox(), a
/// scoped IPublishEndpoint stages onto that context and flushes only when it is saved — and
/// nothing in Payment saves BookingsDbContext. So a Payment type injecting IPublishEndpoint
/// staged a message that was discarded when the scope disposed: PaymentRefundedEvent vanished and
/// the driver's wallet reversal silently never ran, while unit tests mocking IPublishEndpoint
/// happily asserted the publish had happened (GitLab #27). The fix then was IBus, and this test
/// enforced it.
///
/// IBus publishes immediately but stores nothing, so a RabbitMQ outage lost the message with no
/// record it was ever meant to be sent. #65 registered a MassTransit EF outbox on PaymentDbContext,
/// making IPublishEndpoint both correct and durable — until it turned out MassTransit 8.x supports
/// only one DbContext per bus for that outbox, and running it alongside BookingsDbContext's crashed
/// the host (two BusOutboxDeliveryService loops sharing one IBusOutboxNotification singleton).
///
/// Payment now has its own hand-rolled outbox instead (mirroring the Drivers module's
/// IDriverOutboxPublisher), independent of MassTransit's bus-outbox machinery. The rule is the
/// same shape as before, just through a different door: inject IPaymentOutboxPublisher, and
/// publish through it **before** the SaveChanges that commits.
///
/// A message staged after the last save is never flushed — the same silent-drop failure as #27,
/// reached from the opposite direction. That ordering is what
/// <see cref="PaymentOutboxPublishOrderingTests"/> covers; this file covers the wiring.
/// </summary>
public class PaymentModuleOutboxConventionTests
{
    private static readonly Assembly PaymentModule = typeof(RefundPaymentCommandHandler).Assembly;

    private static bool IsConsumer(Type type) =>
        type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>));

    /// <summary>
    /// The outbox only works if the entity is mapped on the context it is registered against.
    /// Without this, staging throws at runtime rather than failing here.
    /// </summary>
    [Fact]
    public void PaymentDbContext_maps_the_outbox_entity()
    {
        var options = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseInMemoryDatabase("outbox-convention-payment-outbox-message")
            .Options;

        using var context = new PaymentDbContext(options);

        Assert.NotNull(context.Model.FindEntityType(typeof(OutboxMessage)));
    }

    /// <summary>
    /// IBus bypasses the outbox entirely. Reintroducing it in this module would publish straight
    /// to the broker again, so the message would be lost on an outage - which is the whole reason
    /// #65 exists. Consumers are exempt: inside a ConsumeContext the resolved IPublishEndpoint is
    /// the consume context's own, which is already correct.
    /// </summary>
    [Fact]
    public void No_payment_module_type_injects_IBus()
    {
        var offenders = PaymentModule
            .GetTypes()
            .Where(t => !IsConsumer(t))
            .SelectMany(t => t.GetConstructors())
            .Where(c => c.GetParameters().Any(p => p.ParameterType == typeof(IBus)))
            .Select(c => c.DeclaringType!.FullName!)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These Payment-module types inject IBus, which publishes straight to the broker and "
            + "keeps nothing, so the message is lost if RabbitMQ is unavailable. Inject "
            + "IPaymentOutboxPublisher instead and publish BEFORE the SaveChanges that commits "
            + "(see GitLab #65):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders.Select(o => "  - " + o)));
    }

    /// <summary>
    /// Also bypasses Payment's own outbox: a plain IPublishEndpoint in a non-consumer type has no
    /// designated DbContext to stage onto in this MassTransit version (the whole reason the
    /// module moved to a hand-rolled outbox), so it would silently fall back to whatever the
    /// ambient bus configuration resolves - which is not guaranteed to be durable.
    /// </summary>
    [Fact]
    public void No_payment_module_type_injects_the_ambient_IPublishEndpoint()
    {
        var offenders = PaymentModule
            .GetTypes()
            .Where(t => !IsConsumer(t))
            .SelectMany(t => t.GetConstructors())
            .Where(c => c.GetParameters().Any(p => p.ParameterType == typeof(IPublishEndpoint)))
            .Select(c => c.DeclaringType!.FullName!)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These Payment-module types inject the ambient IPublishEndpoint. Inject "
            + "IPaymentOutboxPublisher instead, so the message stages transactionally onto "
            + "PaymentDbContext regardless of what else MassTransit has registered:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders.Select(o => "  - " + o)));
    }

    /// <summary>
    /// The two handlers that emit PaymentRefundedEvent, the message that reverses a driver's
    /// wallet. These are the ones where losing a publish costs real money.
    /// </summary>
    [Theory]
    [InlineData(typeof(RefundPaymentCommandHandler))]
    [InlineData(typeof(ProcessRefundWebhookHandler))]
    public void Refund_handlers_publish_through_the_outbox(Type handler)
    {
        var parameters = handler.GetConstructors().Single().GetParameters();

        Assert.Contains(parameters, p => p.ParameterType == typeof(IPaymentOutboxPublisher));
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(IBus));
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(IPublishEndpoint));
    }
}
