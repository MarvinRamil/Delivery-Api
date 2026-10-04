using System.Reflection;
using BeeLogistics.Modules.Messaging.Application.Consumers;
using MassTransit;
using Xunit;

namespace BeeLogistics.Tests.Messaging;

/// <summary>
/// Enforces the publishing convention for the Messaging module.
/// </summary>
/// <remarks>
/// <para>
/// The MassTransit EF outbox is bound to <c>BookingsDbContext</c> and, with <c>UseBusOutbox()</c>,
/// a scoped <c>IPublishEndpoint</c> stages onto <em>that</em> context and flushes only when it is
/// saved. Nothing in Messaging saves <c>BookingsDbContext</c>, so a Messaging type injecting
/// <c>IPublishEndpoint</c> would stage a message that is silently discarded when the scope
/// disposes — and a unit test mocking <c>IPublishEndpoint</c> would cheerfully assert the publish
/// happened.
/// </para>
/// <para>
/// That exact failure already cost this codebase a driver wallet reversal (GitLab #27); see
/// <c>PaymentModuleOutboxConventionTests</c> for the full history. Messaging must use
/// <c>IBus</c> or <c>IDirectBusPublisher</c> if it ever needs to publish.
/// </para>
/// </remarks>
public class MessagingModuleConventionTests
{
    private static readonly Assembly MessagingAssembly = typeof(BookingChatRoomRequestedConsumer).Assembly;

    [Fact]
    public void No_messaging_type_injects_IPublishEndpoint()
    {
        var offenders = MessagingAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .SelectMany(t => t.GetConstructors().Select(c => (Type: t, Ctor: c)))
            .Where(x => x.Ctor.GetParameters().Any(p => p.ParameterType == typeof(IPublishEndpoint)))
            .Select(x => x.Type.FullName!)
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0,
            "These Messaging types inject IPublishEndpoint, whose messages would be staged onto " +
            "BookingsDbContext's outbox and silently dropped. Use IBus or IDirectBusPublisher:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void Every_messaging_consumer_is_registered_in_Program()
    {
        // A consumer that exists but is never added to AddMassTransit is dead code that looks
        // alive: the event is published, nothing consumes it, and no error is raised anywhere.
        var consumers = MessagingAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>)))
            .Select(t => t.Name)
            .ToList();

        Assert.NotEmpty(consumers);

        var programPath = Path.Combine(RepoRoot(), "src", "BeeLogistics.Api", "Program.cs");
        var program = File.ReadAllText(programPath);

        var missing = consumers.Where(c => !program.Contains($"AddConsumer<BeeLogistics.Modules.Messaging.Application.Consumers.{c}>", StringComparison.Ordinal)).ToList();

        Assert.True(missing.Count == 0,
            "These Messaging consumers are not registered in Program.cs, so the events they handle " +
            "would be published and silently ignored:\n  " + string.Join("\n  ", missing));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "bee-app-backend.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
