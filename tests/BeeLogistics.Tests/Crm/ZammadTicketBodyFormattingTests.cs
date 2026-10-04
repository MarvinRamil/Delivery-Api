using System.Reflection;
using BeeLogistics.Modules.CRM.Domain;
using BeeLogistics.Modules.CRM.Infrastructure;
using Xunit;

namespace BeeLogistics.Tests.Crm;

/// <summary>
/// The body we hand Zammad when a ticket is created (issue #63).
///
/// It used to be built with &lt;strong&gt; and &lt;br/&gt; while the article declared no
/// content_type. Zammad defaults that to text/plain, so it stored the markup and agents read the
/// tags instead of the ticket. Drivers never saw it - the app runs the article through stripHtml
/// - so the only people affected were the ones working the queue.
///
/// The fix is plain text rather than declaring the article text/html, because the description is
/// user-supplied and interpolated straight into this string. These tests pin both halves: no
/// markup goes out, and nothing an agent needs was dropped on the way to removing it.
/// </summary>
public class ZammadTicketBodyFormattingTests
{
    /// <summary>
    /// FormatTicketBody is a private static helper, reached by reflection the same way
    /// ZammadWebhookArticleDeliveryTests reaches SelectDeliverableReply. Nothing on the
    /// service instance is touched, so no HTTP client or settings are needed.
    /// </summary>
    private static string Format(SupportTicket ticket, string name = "Juan Dela Cruz",
        string email = "juan@example.com", string userType = "driver")
    {
        var method = typeof(ZammadService)
            .GetMethod("FormatTicketBody", BindingFlags.NonPublic | BindingFlags.Static)!;

        return (string)method.Invoke(null, new object?[] { ticket, name, email, userType })!;
    }

    private static SupportTicket Ticket(string? description = "The app crashed on pickup") => new()
    {
        TicketNumber = "TKT-20260808-2788FA",
        Subject = "App crash",
        UserId = "380c19af-ebec-4512-8c4c-b5a103c08ceb",
        Description = description,
        Category = TicketCategory.General,
        Priority = TicketPriority.Normal
    };

    // ------------------------------------------------------------------ no markup escapes

    [Fact]
    public void The_body_carries_no_html_tags()
    {
        var body = Format(Ticket());

        Assert.DoesNotContain("<", body);
        Assert.DoesNotContain(">", body);
    }

    [Theory]
    [InlineData("<strong>")]
    [InlineData("<br/>")]
    public void The_markup_that_agents_were_reading_is_gone(string markup)
    {
        Assert.DoesNotContain(markup, Format(Ticket()));
    }

    [Fact]
    public void A_description_containing_markup_is_passed_through_as_the_text_the_user_typed()
    {
        // The reason this stays text/plain. A driver can type anything, and declaring the
        // article as HTML would render it in the agent console. Here it must survive verbatim,
        // neither executed nor mangled.
        const string typed = "<script>alert(1)</script> the button did nothing";

        var body = Format(Ticket(typed));

        Assert.Contains(typed, body);
    }

    // -------------------------------------------------------- nothing an agent needs is lost

    [Fact]
    public void Every_field_an_agent_triages_on_survives_the_move_to_plain_text()
    {
        var ticket = Ticket();
        ticket.BookingId = Guid.NewGuid();

        var body = Format(ticket, "Juan Dela Cruz", "juan@example.com", "driver");

        Assert.Contains("BeeApp Support Ticket", body);
        Assert.Contains("TKT-20260808-2788FA", body);
        Assert.Contains("Juan Dela Cruz", body);
        Assert.Contains("driver", body);
        // Email and user id are searchable text on purpose: Zammad indexes article bodies, so an
        // agent can find every ticket from one person even when the customer record was matched
        // to the wrong account.
        Assert.Contains("juan@example.com", body);
        Assert.Contains("380c19af-ebec-4512-8c4c-b5a103c08ceb", body);
        Assert.Contains("General", body);
        Assert.Contains("Normal", body);
        Assert.Contains(ticket.BookingId.Value.ToString(), body);
        Assert.Contains("The app crashed on pickup", body);
    }

    [Fact]
    public void Labels_stay_on_their_own_lines_now_that_the_line_breaks_are_real()
    {
        // The old body was one long string relying on <br/> for structure. With those gone the
        // newlines are what make it readable, so an agent scanning the block still gets one
        // field per line rather than a run-on paragraph.
        var lines = Format(Ticket())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToList();

        Assert.Contains("Ticket #: TKT-20260808-2788FA", lines);
        Assert.Contains("Submitted by: Juan Dela Cruz (driver)", lines);
        Assert.Contains("Email: juan@example.com", lines);
        Assert.Contains("Category: General", lines);
        Assert.Contains("Priority: Normal", lines);
    }

    [Fact]
    public void An_absent_booking_leaves_no_empty_label_behind()
    {
        var body = Format(Ticket());

        Assert.DoesNotContain("Related Booking", body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_description_leaves_no_empty_label_behind(string? description)
    {
        var body = Format(Ticket(description));

        Assert.DoesNotContain("Description:", body);
        // The metadata block is still worth sending on its own - a ticket with only a subject
        // is still a ticket an agent has to work.
        Assert.Contains("TKT-20260808-2788FA", body);
    }
}
