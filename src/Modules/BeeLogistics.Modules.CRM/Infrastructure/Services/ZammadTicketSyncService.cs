using BeeLogistics.Modules.CRM.Domain;
using BeeLogistics.Shared.Contracts;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.CRM.Infrastructure.Services;

/// <summary>
/// Pushes support tickets to Zammad that the inline attempt at creation time did not manage to
/// send, so a Zammad outage defers a ticket rather than losing it.
/// </summary>
/// <remarks>
/// Before this existed, a ticket whose sync timed out or threw was logged as "saved locally" and
/// never retried: <c>ZammadTicketId</c> stayed null forever and support never saw it.
///
/// Two things make the retry safe:
/// <list type="bullet">
/// <item>The inline attempt only waits 3 seconds, so a slower call can still succeed after we
/// stop listening, leaving a Zammad ticket whose id we never recorded. Every retry therefore
/// searches Zammad by ticket number first and adopts an existing ticket instead of creating a
/// second one.</item>
/// <item>Attempts are counted and backed off, and a ticket that keeps failing is abandoned by the
/// job and logged loudly rather than retried forever.</item>
/// </list>
/// </remarks>
public class ZammadTicketSyncService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ZammadTicketSyncService> _logger;

    /// <summary>Give up after this many attempts and surface the ticket instead.</summary>
    public const int MaxAttempts = 8;

    /// <summary>Tickets are only retried this long after their last attempt.</summary>
    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
    };

    private const int BatchSize = 50;

    public ZammadTicketSyncService(IServiceProvider serviceProvider, ILogger<ZammadTicketSyncService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 1)]
    public async Task SyncPendingTicketsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        var zammad = scope.ServiceProvider.GetRequiredService<ZammadService>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var now = DateTime.UtcNow;

        var pending = await context.SupportTickets
            .Where(t => t.ZammadTicketId == null && t.ZammadSyncAttempts < MaxAttempts)
            .OrderBy(t => t.LastZammadSyncAttemptAt)
            .Take(BatchSize)
            .ToListAsync();

        if (pending.Count == 0)
            return;

        var due = pending.Where(t => IsDue(t, now)).ToList();
        if (due.Count == 0)
            return;

        var synced = 0;
        foreach (var ticket in due)
        {
            try
            {
                ticket.ZammadSyncAttempts++;
                ticket.LastZammadSyncAttemptAt = now;

                // A previous attempt may have succeeded after we stopped waiting for it.
                var existing = await zammad.FindTicketByNumberAsync(ticket.TicketNumber);
                if (existing.HasValue)
                {
                    ticket.ZammadTicketId = existing.Value;
                    ticket.LastZammadSyncError = null;
                    synced++;
                    _logger.LogInformation(
                        "zammad Ticket {TicketNumber} already existed in Zammad as {ZammadId}; adopted instead of recreating",
                        ticket.TicketNumber, existing.Value);
                    continue;
                }

                var email = await ResolveEmailAsync(ticket, mediator, context);
                if (string.IsNullOrWhiteSpace(email))
                {
                    // Never substitute a placeholder: it produces a plausible-looking ticket
                    // filed against a shared fake customer whose replies reach nobody.
                    ticket.LastZammadSyncError = "No email could be resolved for the ticket author";
                    continue;
                }

                var zammadId = await zammad.CreateTicketAsync(
                    ticket, email, ticket.UserFullName ?? email, ticket.UserType);

                if (zammadId.HasValue)
                {
                    ticket.ZammadTicketId = zammadId.Value;
                    ticket.LastZammadSyncError = null;
                    synced++;
                }
                else
                {
                    ticket.LastZammadSyncError = "Zammad returned no ticket id";
                }
            }
            catch (Exception ex)
            {
                ticket.LastZammadSyncError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                _logger.LogWarning(ex, "zammad Retry failed for ticket {TicketNumber}", ticket.TicketNumber);
            }
        }

        await context.SaveChangesAsync();

        var exhausted = due.Count(t => t.ZammadTicketId == null && t.ZammadSyncAttempts >= MaxAttempts);
        if (exhausted > 0)
        {
            _logger.LogError(
                "zammad {Count} ticket(s) gave up after {MaxAttempts} sync attempts and will not be retried. Ticket numbers: {TicketNumbers}",
                exhausted, MaxAttempts,
                string.Join(", ", due.Where(t => t.ZammadTicketId == null && t.ZammadSyncAttempts >= MaxAttempts)
                    .Take(20).Select(t => t.TicketNumber)));
        }

        _logger.LogInformation(
            "zammad Retry pass: {Synced}/{Attempted} ticket(s) synced ({Pending} still pending)",
            synced, due.Count, pending.Count - synced);
    }

    /// <summary>
    /// Whether enough time has passed since this ticket's last attempt. Backoff is indexed by
    /// attempt count and holds at the last tier, so a long outage does not produce a retry storm.
    /// </summary>
    private static bool IsDue(SupportTicket ticket, DateTime now)
    {
        if (ticket.LastZammadSyncAttemptAt is null)
            return true;

        var index = Math.Min(Math.Max(ticket.ZammadSyncAttempts - 1, 0), Backoff.Length - 1);
        return ticket.LastZammadSyncAttemptAt.Value + Backoff[index] <= now;
    }

    /// <summary>
    /// The author's email: whatever was stored, else the linked customer profile, else Identity.
    /// </summary>
    private static async Task<string?> ResolveEmailAsync(SupportTicket ticket, IMediator mediator, CrmDbContext context)
    {
        if (!string.IsNullOrWhiteSpace(ticket.UserEmail))
            return ticket.UserEmail;

        if (ticket.CustomerProfileId.HasValue)
        {
            var profile = await context.CustomerProfiles
                .FirstOrDefaultAsync(p => p.Id == ticket.CustomerProfileId.Value);
            if (!string.IsNullOrWhiteSpace(profile?.Email))
            {
                ticket.UserEmail ??= profile.Email;
                ticket.UserFullName ??= profile.FullName;
                return ticket.UserEmail;
            }
        }

        if (!string.IsNullOrWhiteSpace(ticket.UserId))
        {
            var contact = await mediator.Send(new GetUserContactQuery(ticket.UserId));
            if (contact.IsSuccess && contact.Value != null && !string.IsNullOrWhiteSpace(contact.Value.Email))
            {
                ticket.UserEmail ??= contact.Value.Email;
                ticket.UserFullName ??= contact.Value.FullName;
                return ticket.UserEmail;
            }
        }

        return null;
    }
}
