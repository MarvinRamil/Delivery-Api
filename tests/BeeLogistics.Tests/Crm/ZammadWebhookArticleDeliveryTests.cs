using System.Reflection;
using BeeLogistics.Modules.CRM.Application.Handlers;
using BeeLogistics.Modules.CRM.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeeLogistics.Tests.Crm;

/// <summary>
/// Which Zammad articles reach the person who raised the ticket (issue #61).
///
/// The webhook is the only path by which an agent's reply becomes a push, and it is fed by an
/// external system that retries. Three things must never happen: an internal agent-to-agent note
/// reaching a driver, the user's own message coming back to them as an incoming reply, and one
/// reply arriving twice. Each has its own guard and each is pinned here.
/// </summary>
public class ZammadWebhookArticleDeliveryTests
{
    /// <summary>
    /// SelectDeliverableReply is a private instance method, reached by reflection the same way
    /// ZammadTicketSyncBackoffTests reaches IsDue. Only _logger is touched, so the context and
    /// notification service are never constructed.
    /// </summary>
    private static ZammadWebhookArticle? Select(SupportTicket ticket, ZammadWebhookArticle? article)
    {
        var handler = new ProcessZammadWebhookHandler(
            context: null!,
            notifications: null!,
            logger: NullLogger<ProcessZammadWebhookHandler>.Instance);

        var method = typeof(ProcessZammadWebhookHandler)
            .GetMethod("SelectDeliverableReply", BindingFlags.NonPublic | BindingFlags.Instance)!;

        return (ZammadWebhookArticle?)method.Invoke(handler, new object?[] { ticket, article });
    }

    private static SupportTicket Ticket(int? lastArticleId = null) => new()
    {
        TicketNumber = "TKT-TEST-000001",
        Subject = "test",
        UserId = "user-1",
        LastZammadArticleId = lastArticleId
    };

    private static ZammadWebhookArticle Article(
        int id = 100, string sender = "Agent", bool isInternal = false) =>
        new(id, "the reply body", "text/plain", sender, isInternal, "Agent <a@example.com>", DateTime.UtcNow);

    [Fact]
    public void An_agent_reply_is_delivered()
    {
        var result = Select(Ticket(), Article());
        Assert.NotNull(result);
        Assert.Equal(100, result!.Id);
    }

    [Fact]
    public void The_first_reply_on_a_ticket_with_no_high_water_mark_is_delivered()
    {
        // Tickets predating this feature have a null mark. They must deliver their next reply,
        // not treat "nothing recorded" as "already sent".
        Assert.NotNull(Select(Ticket(lastArticleId: null), Article()));
    }

    [Fact]
    public void An_internal_note_is_never_delivered()
    {
        Assert.Null(Select(Ticket(), Article(isInternal: true)));
    }

    [Fact]
    public void An_internal_note_from_an_agent_is_still_not_delivered()
    {
        // The internal check has to come first: an internal note is written by an agent, so a
        // sender-only check would let it straight through to the driver.
        Assert.Null(Select(Ticket(), Article(sender: "Agent", isInternal: true)));
    }

    [Theory]
    [InlineData("Customer")]   // our own AddArticleAsync post, echoed back
    [InlineData("System")]
    [InlineData(null)]
    public void Only_agent_articles_are_delivered(string? sender)
    {
        Assert.Null(Select(Ticket(), Article(sender: sender!)));
    }

    [Fact]
    public void Sender_matching_ignores_case()
    {
        Assert.NotNull(Select(Ticket(), Article(sender: "agent")));
    }

    [Theory]
    [InlineData(100, 100)]   // the same delivery replayed
    [InlineData(100, 99)]    // an older article arriving late
    public void An_already_delivered_article_is_not_delivered_again(int mark, int articleId)
    {
        Assert.Null(Select(Ticket(lastArticleId: mark), Article(id: articleId)));
    }

    [Fact]
    public void An_article_newer_than_the_mark_is_delivered()
    {
        var result = Select(Ticket(lastArticleId: 100), Article(id: 101));
        Assert.NotNull(result);
        Assert.Equal(101, result!.Id);
    }

    [Fact]
    public void A_payload_with_no_article_yields_nothing()
    {
        // State-only and owner-only webhooks are the common case and must stay silent.
        Assert.Null(Select(Ticket(), null));
    }
}
