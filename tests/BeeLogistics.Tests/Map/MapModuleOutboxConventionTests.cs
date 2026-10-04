using System.Reflection;
using BeeLogistics.Modules.Map.Application.Handlers;
using BeeLogistics.Modules.Map.Infrastructure.Services;
using MassTransit;
using Xunit;

namespace BeeLogistics.Tests.Map;

/// <summary>
/// Guards the invariant behind GitLab #37, the same one PaymentModuleOutboxConventionTests guards
/// for the Payment module.
///
/// The MassTransit EF outbox is registered on BookingsDbContext with UseBusOutbox()
/// (src/BeeLogistics.Api/Program.cs). With the bus outbox active, a scoped IPublishEndpoint
/// resolved outside a ConsumeContext does not publish immediately - it stages the message onto
/// BookingsDbContext and flushes only when *that* context is saved.
///
/// Nothing in the Map module saves BookingsDbContext, and the location handlers touch no database
/// at all. So UpdateDriverLocationCommandHandler, invoked from an HTTP request scope, staged every
/// location event onto a context nobody saved and the message was discarded when the scope
/// disposed. Both /api/locations/update and /api/locations/batch published nothing for as long as
/// that constructor read IPublishEndpoint, while returning 200 OK to the driver app.
///
/// Consumers are exempt: inside a ConsumeContext the scoped IPublishEndpoint *is* the consume
/// context's endpoint, which publishes correctly. GeofenceCheckConsumer relies on that and is fine.
/// </summary>
public class MapModuleOutboxConventionTests
{
    private static readonly Assembly MapModule = typeof(UpdateDriverLocationCommandHandler).Assembly;

    private static bool IsConsumer(Type type) =>
        type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>));

    [Fact]
    public void No_non_consumer_map_type_injects_IPublishEndpoint()
    {
        var offenders = MapModule
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
            "These Map-module types inject IPublishEndpoint outside a ConsumeContext, which stages "
            + "onto the BookingsDbContext outbox and is dropped because this module never saves "
            + "that context. Publish through ILocationIngestService (or IBus) instead - see "
            + "GitLab #37:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders.Select(o => "  - " + o)));
    }

    [Fact]
    public void Location_ingest_service_publishes_through_IBus()
    {
        var parameters = typeof(LocationIngestService).GetConstructors().Single().GetParameters();

        Assert.Contains(parameters, p => p.ParameterType == typeof(IBus));
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(IPublishEndpoint));
    }

    /// <summary>
    /// The ingest service is the module's single publish site. If a second one appears, the
    /// convention above has to be re-reasoned about rather than silently inherited.
    /// </summary>
    [Fact]
    public void Only_the_ingest_service_injects_IBus()
    {
        var busInjectors = MapModule
            .GetTypes()
            .Where(t => !IsConsumer(t))
            .SelectMany(t => t.GetConstructors())
            .Where(c => c.GetParameters().Any(p => p.ParameterType == typeof(IBus)))
            .Select(c => c.DeclaringType!)
            .Distinct()
            .ToList();

        Assert.Equal(new[] { typeof(LocationIngestService) }, busInjectors);
    }
}
