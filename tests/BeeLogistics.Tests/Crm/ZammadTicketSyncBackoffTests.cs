using System.Reflection;
using BeeLogistics.Modules.CRM.Domain;
using BeeLogistics.Modules.CRM.Infrastructure.Services;
using Xunit;

namespace BeeLogistics.Tests.Crm;

/// <summary>
/// Retry backoff for the Zammad ticket sync job (issue #57).
///
/// The job runs every two minutes, so what stops a Zammad outage becoming a retry storm is
/// entirely this per-ticket schedule. The tier must widen with attempts and hold at the last
/// tier rather than running off the end of the array.
/// </summary>
public class ZammadTicketSyncBackoffTests
{
    private static bool IsDue(SupportTicket ticket, DateTime now)
    {
        // IsDue is a private static, matching how BookingBroadcastQueueServiceTests reaches
        // ComputeNextPulseAt. Reflection keeps the production surface unchanged.
        var method = typeof(ZammadTicketSyncService)
            .GetMethod("IsDue", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, new object[] { ticket, now })!;
    }

    private static SupportTicket Ticket(int attempts, DateTime? lastAttempt) => new()
    {
        TicketNumber = "TKT-TEST-000001",
        Subject = "test",
        ZammadSyncAttempts = attempts,
        LastZammadSyncAttemptAt = lastAttempt
    };

    [Fact]
    public void A_ticket_never_attempted_is_due_immediately()
    {
        Assert.True(IsDue(Ticket(attempts: 0, lastAttempt: null), DateTime.UtcNow));
    }

    [Theory]
    // attempts, minutes since last attempt, expected due
    [InlineData(1, 0, false)]      // first retry waits 1 min
    [InlineData(1, 2, true)]
    [InlineData(2, 3, false)]      // then 5 min
    [InlineData(2, 6, true)]
    [InlineData(3, 10, false)]     // then 15 min
    [InlineData(3, 16, true)]
    [InlineData(4, 30, false)]     // then 1 hour
    [InlineData(4, 61, true)]
    [InlineData(5, 120, false)]    // then 6 hours
    [InlineData(5, 361, true)]
    public void Backoff_widens_with_each_attempt(int attempts, int minutesSince, bool expectedDue)
    {
        var now = DateTime.UtcNow;
        var ticket = Ticket(attempts, now.AddMinutes(-minutesSince));

        Assert.Equal(expectedDue, IsDue(ticket, now));
    }

    [Fact]
    public void Backoff_holds_at_the_last_tier_instead_of_overrunning_the_array()
    {
        var now = DateTime.UtcNow;

        // Attempt counts past the number of configured tiers must clamp, not throw.
        foreach (var attempts in new[] { 6, 7, 8, 50 })
        {
            Assert.False(IsDue(Ticket(attempts, now.AddHours(-1)), now));
            Assert.True(IsDue(Ticket(attempts, now.AddHours(-7)), now));
        }
    }

    [Fact]
    public void The_attempt_ceiling_leaves_room_for_a_long_outage()
    {
        // 1 + 5 + 15 + 60 + 6h * remaining tiers: the ceiling should span well over a day so a
        // weekend outage does not exhaust a ticket's retries before anyone can react.
        var spanHours = (1 + 5 + 15) / 60.0 + 1 + (ZammadTicketSyncService.MaxAttempts - 4) * 6.0;

        Assert.True(spanHours > 24,
            $"Retry ceiling only spans {spanHours:F1}h; a ticket would be abandoned before a long outage ends.");
    }
}
