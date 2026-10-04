using System.Text;
using BeeLogistics.Modules.CRM.Application.DTOs;
using BeeLogistics.Modules.CRM.Domain;
using BeeLogistics.Modules.CRM.Infrastructure;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Hubs;
using BeeLogistics.Shared.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.CRM.Application.Handlers;

// Queries
public record GetTicketsQuery(TicketStatus? Status = null) : IRequest<Result<List<SupportTicketDto>>>;
public record GetTicketQuery(Guid Id, bool IncludeZammad = false) : IRequest<Result<SupportTicketDto>>;
public record GetTicketWithZammadQuery(Guid Id) : IRequest<Result<SupportTicketWithZammadDto>>;
public record GetCustomerTicketsQuery(Guid CustomerProfileId) : IRequest<Result<List<SupportTicketDto>>>;
public record GetMyTicketsQuery(string UserId) : IRequest<Result<List<SupportTicketDto>>>;

// Commands
public record CreateTicketCommand(CreateTicketDto Data, string UserId, string? UserEmail = null, string? UserName = null, string? IdempotencyKey = null) : IRequest<Result<SupportTicketDto>>;
public record UpdateTicketCommand(Guid Id, UpdateTicketDto Data) : IRequest<Result<SupportTicketDto>>;
public record AssignTicketCommand(Guid Id, string AssignedToUserId, string AssignedToName) : IRequest<Result<SupportTicketDto>>;
public record ResolveTicketCommand(Guid Id, string Resolution) : IRequest<Result<SupportTicketDto>>;
public record AddTicketCommentCommand(Guid Id, string Body, string UserId, string? UserEmail = null, string? UserName = null) : IRequest<Result<Unit>>;
public record ProcessZammadWebhookCommand(int ZammadTicketId, string? State, string? OwnerName, ZammadWebhookArticle? Article = null) : IRequest<Unit>;

/// <summary>
/// The article Zammad sends alongside the ticket when the event was someone writing something.
/// Absent on pure state or owner changes, hence nullable on the command.
/// </summary>
/// <param name="Sender">"Agent", "Customer" or "System" - who wrote it, in Zammad's vocabulary.</param>
/// <param name="Internal">Agent-to-agent note. Never leaves the helpdesk.</param>
public record ZammadWebhookArticle(
    int Id,
    string Body,
    string? ContentType,
    string? Sender,
    bool Internal,
    string? From,
    DateTime? CreatedAt);

// Handlers
public class GetTicketsHandler : IRequestHandler<GetTicketsQuery, Result<List<SupportTicketDto>>>
{
    private readonly CrmDbContext _context;
    public GetTicketsHandler(CrmDbContext context) => _context = context;

    public async Task<Result<List<SupportTicketDto>>> Handle(GetTicketsQuery request, CancellationToken ct)
    {
        var query = _context.SupportTickets.Include(t => t.CustomerProfile).AsQueryable();

        if (request.Status.HasValue)
            query = query.Where(t => t.Status == request.Status.Value);

        var tickets = await query.OrderByDescending(t => t.CreatedAt).ToListAsync(ct);

        return Result.Ok(tickets.Select(ToDto).ToList());
    }

    internal static SupportTicketDto ToDto(SupportTicket t) => new(
        t.Id, t.TicketNumber, t.CustomerProfileId,
        t.CustomerProfile?.FullName ?? t.UserFullName, 
        t.CustomerProfile?.Email ?? t.UserEmail,
        t.ConversationId, t.BookingId, t.Subject, t.Description,
        t.Category.ToString(), t.Priority.ToString(), t.Status.ToString(),
        t.AssignedToUserId, t.AssignedToName, t.CreatedAt, t.ResolvedAt, t.Resolution,
        t.ZammadTicketId, t.UserType
    );
}

public class GetTicketHandler : IRequestHandler<GetTicketQuery, Result<SupportTicketDto>>
{
    private readonly CrmDbContext _context;
    public GetTicketHandler(CrmDbContext context) => _context = context;

    public async Task<Result<SupportTicketDto>> Handle(GetTicketQuery request, CancellationToken ct)
    {
        var ticket = await _context.SupportTickets
            .Include(t => t.CustomerProfile)
            .FirstOrDefaultAsync(t => t.Id == request.Id, ct);

        if (ticket == null)
            return Result.Fail<SupportTicketDto>("Ticket not found");

        return Result.Ok(GetTicketsHandler.ToDto(ticket));
    }
}

public class GetTicketWithZammadHandler : IRequestHandler<GetTicketWithZammadQuery, Result<SupportTicketWithZammadDto>>
{
    private readonly CrmDbContext _context;
    private readonly ZammadService _zammadService;

    public GetTicketWithZammadHandler(CrmDbContext context, ZammadService zammadService)
    {
        _context = context;
        _zammadService = zammadService;
    }

    public async Task<Result<SupportTicketWithZammadDto>> Handle(GetTicketWithZammadQuery request, CancellationToken ct)
    {
        var ticket = await _context.SupportTickets
            .Include(t => t.CustomerProfile)
            .FirstOrDefaultAsync(t => t.Id == request.Id, ct);

        if (ticket == null)
            return Result.Fail<SupportTicketWithZammadDto>("Ticket not found");

        var dto = GetTicketsHandler.ToDto(ticket);
        ZammadDetailsDto? zammad = null;

        if (ticket.ZammadTicketId.HasValue)
        {
            var zammadData = await _zammadService.GetTicketWithArticlesAsync(ticket.ZammadTicketId.Value, ct);
            if (zammadData != null)
            {
                zammad = new ZammadDetailsDto(
                    zammadData.Title,
                    zammadData.State,
                    zammadData.Articles.Select(a => new ZammadArticleDto(
                        a.Body, a.Subject, a.From, a.Sender, a.CreatedAt, a.ContentType, a.Internal)).ToList());

                // Sync status from Zammad when Zammad says closed
                if (zammadData.State.Equals("closed", StringComparison.OrdinalIgnoreCase) &&
                    ticket.Status != TicketStatus.Closed && ticket.Status != TicketStatus.Resolved)
                {
                    ticket.Status = TicketStatus.Closed;
                    ticket.ResolvedAt ??= DateTime.UtcNow;
                    await _context.SaveChangesAsync(ct);
                    dto = GetTicketsHandler.ToDto(ticket);
                }
            }
        }

        return Result.Ok(new SupportTicketWithZammadDto(dto, zammad));
    }
}

public class GetMyTicketsHandler : IRequestHandler<GetMyTicketsQuery, Result<List<SupportTicketDto>>>
{
    private readonly CrmDbContext _context;
    private readonly ZammadService _zammadService;

    public GetMyTicketsHandler(CrmDbContext context, ZammadService zammadService)
    {
        _context = context;
        _zammadService = zammadService;
    }

    public async Task<Result<List<SupportTicketDto>>> Handle(GetMyTicketsQuery request, CancellationToken ct)
    {
        var profile = await _context.CustomerProfiles
            .FirstOrDefaultAsync(p => p.UserId == request.UserId, ct);

        // My tickets = as customer (by profile) OR as driver (by UserId)
        var tickets = await _context.SupportTickets
            .Include(t => t.CustomerProfile)
            .Where(t =>
                (profile != null && t.CustomerProfileId == profile.Id) ||
                t.UserId == request.UserId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        // Sync status from Zammad for tickets that have a Zammad ID (e.g. closed in Zammad)
        var toSync = tickets.Where(t => t.ZammadTicketId.HasValue).Take(15).ToList();
        if (toSync.Count > 0)
        {
            var syncTasks = toSync.Select(async t =>
            {
                var state = await _zammadService.GetTicketStateAsync(t.ZammadTicketId!.Value, ct);
                return (Ticket: t, State: state);
            }).ToList();
            var results = await Task.WhenAll(syncTasks);
            var anyUpdated = false;
            foreach (var (ticket, state) in results)
            {
                if (state?.Equals("closed", StringComparison.OrdinalIgnoreCase) == true &&
                    ticket.Status != TicketStatus.Closed && ticket.Status != TicketStatus.Resolved)
                {
                    ticket.Status = TicketStatus.Closed;
                    ticket.ResolvedAt ??= DateTime.UtcNow;
                    anyUpdated = true;
                }
            }
            if (anyUpdated)
                await _context.SaveChangesAsync(ct);
        }

        return Result.Ok(tickets.Select(GetTicketsHandler.ToDto).ToList());
    }
}

public class CreateTicketHandler : IRequestHandler<CreateTicketCommand, Result<SupportTicketDto>>
{
    private const string IdempotencyKeyPrefix = "crm:ticket:idempotency:";
    private static readonly TimeSpan IdempotencyWindow = TimeSpan.FromHours(24);

    private readonly CrmDbContext _context;
    private readonly ZammadService _zammadService;
    private readonly IDistributedCache _cache;
    private readonly IMediator _mediator;
    private readonly ILogger<CreateTicketHandler> _logger;

    public CreateTicketHandler(
        CrmDbContext context,
        ZammadService zammadService,
        IDistributedCache cache,
        IMediator mediator,
        ILogger<CreateTicketHandler> logger)
    {
        _context = context;
        _zammadService = zammadService;
        _cache = cache;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task<Result<SupportTicketDto>> Handle(CreateTicketCommand request, CancellationToken ct)
    {
        // Idempotency: duplicate request (e.g. double-submit or retry) returns existing ticket without creating a second one or syncing to Zammad again.
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var cacheKey = IdempotencyKeyPrefix + request.IdempotencyKey.Trim();
            var existingIdBytes = await _cache.GetAsync(cacheKey, ct);
            if (existingIdBytes != null)
            {
                var existingId = new Guid(Encoding.UTF8.GetString(existingIdBytes));
                var existing = await _context.SupportTickets.Include(t => t.CustomerProfile).FirstOrDefaultAsync(t => t.Id == existingId, ct);
                if (existing != null)
                {
                    _logger.LogDebug("CreateTicket idempotent hit for key {Key}, returning existing ticket {TicketNumber}", request.IdempotencyKey, existing.TicketNumber);
                    return Result.Ok(GetTicketsHandler.ToDto(existing));
                }
            }
        }

        var userType = request.Data.UserType ?? "customer";
        CustomerProfile? profile = null;

        if (request.Data.CustomerProfileId.HasValue)
        {
            profile = await _context.CustomerProfiles
                .FirstOrDefaultAsync(p => p.Id == request.Data.CustomerProfileId.Value, ct);

            if (profile == null && userType == "customer")
                return Result.Fail<SupportTicketDto>("Customer not found");
        }
        else if (userType == "customer")
        {
            return Result.Fail<SupportTicketDto>("CustomerProfileId is required for customer tickets");
        }

        var ticketNumber = $"TKT-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..6].ToUpper()}";

        // SECURITY: Sanitize user-generated content to prevent XSS attacks
        var sanitizedSubject = InputSanitizer.SanitizePlainText(request.Data.Subject, maxLength: 200);
        var sanitizedDescription = InputSanitizer.SanitizePlainText(request.Data.Description, maxLength: 5000);

        var ticket = new SupportTicket
        {
            TicketNumber = ticketNumber,
            CustomerProfileId = request.Data.CustomerProfileId,
            UserId = request.UserId,
            Subject = sanitizedSubject,
            Description = sanitizedDescription,
            Category = request.Data.Category,
            Priority = request.Data.Priority,
            BookingId = request.Data.BookingId,
            Status = TicketStatus.Open,
            UserType = userType,
            UserEmail = request.UserEmail,
            UserFullName = request.UserName
        };

        if (profile != null)
        {
            ticket.UserEmail ??= profile.Email;
            ticket.UserFullName ??= profile.FullName;
            profile.LastContactDate = DateTime.UtcNow;
        }

        // Drivers have no CustomerProfile, so when the JWT claims do not resolve there is
        // nothing above to fall back to and the ticket used to be filed in Zammad under a
        // shared "unknown@example.com" customer. UserId is always present and reliable, so
        // ask Identity directly rather than trusting claim-type mapping.
        if ((ticket.UserEmail == null || ticket.UserFullName == null)
            && !string.IsNullOrWhiteSpace(ticket.UserId))
        {
            var contact = await _mediator.Send(new GetUserContactQuery(ticket.UserId), ct);
            if (contact.IsSuccess && contact.Value != null)
            {
                ticket.UserEmail ??= contact.Value.Email;
                ticket.UserFullName ??= contact.Value.FullName;
            }
            else
            {
                _logger.LogWarning(
                    "Could not resolve contact details for user {UserId} on ticket {TicketNumber}",
                    ticket.UserId, ticketNumber);
            }
        }

        _context.SupportTickets.Add(ticket);
        await _context.SaveChangesAsync(ct);

        // Sync to Zammad — don't block the response. Wait max 3 seconds; if Zammad is slow or
        // unreachable the ticket is already committed above and ZammadTicketSyncService will
        // pick it up, so failing here defers the sync rather than abandoning it.
        ticket.ZammadSyncAttempts++;
        ticket.LastZammadSyncAttemptAt = DateTime.UtcNow;
        try
        {
            if (string.IsNullOrWhiteSpace(ticket.UserEmail))
            {
                // Deliberately not inventing an address. A placeholder produces a Zammad ticket
                // that looks fine, is filed against a shared fake customer, and silently swallows
                // every agent reply. Leaving it unsynced surfaces the problem and the retry job
                // will send it once identity can be resolved.
                ticket.LastZammadSyncError = "No email could be resolved for the ticket author";
                _logger.LogWarning(
                    "zammad Ticket {TicketNumber} has no resolvable author email; deferring sync",
                    ticketNumber);
            }
            else
            {
                var syncTask = _zammadService.CreateTicketAsync(
                    ticket, ticket.UserEmail, ticket.UserFullName ?? ticket.UserEmail, userType);
                // CancellationToken.None: a client disconnect must not be reported as a Zammad failure.
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
                var completed = await Task.WhenAny(syncTask, timeoutTask);

                if (completed == syncTask && syncTask.IsCompletedSuccessfully && syncTask.Result.HasValue)
                {
                    ticket.ZammadTicketId = syncTask.Result.Value;
                    ticket.LastZammadSyncError = null;
                    _logger.LogInformation(
                        "zammad Ticket {TicketNumber} synced with ID {ZammadId}",
                        ticketNumber, syncTask.Result.Value);
                }
                else if (completed == timeoutTask)
                {
                    // syncTask is still running and may yet succeed. Its id would be lost, which
                    // is why the retry job searches Zammad by ticket number before creating.
                    ticket.LastZammadSyncError = "Timed out after 3s";
                    _logger.LogWarning(
                        "zammad Sync for ticket {TicketNumber} did not complete within 3s; queued for retry",
                        ticketNumber);
                }
                else
                {
                    ticket.LastZammadSyncError = "Zammad returned no ticket id";
                }
            }
        }
        catch (Exception ex)
        {
            ticket.LastZammadSyncError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            _logger.LogWarning(ex,
                "zammad Failed to sync ticket {TicketNumber}; queued for retry",
                ticketNumber);
        }

        await _context.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var cacheKey = IdempotencyKeyPrefix + request.IdempotencyKey.Trim();
            await _cache.SetAsync(cacheKey, Encoding.UTF8.GetBytes(ticket.Id.ToString()), new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = IdempotencyWindow }, ct);
        }

        return Result.Ok(GetTicketsHandler.ToDto(ticket));
    }
}

public class UpdateTicketHandler : IRequestHandler<UpdateTicketCommand, Result<SupportTicketDto>>
{
    private readonly CrmDbContext _context;
    private readonly ZammadService _zammadService;
    private readonly ILogger<UpdateTicketHandler> _logger;

    public UpdateTicketHandler(
        CrmDbContext context,
        ZammadService zammadService,
        ILogger<UpdateTicketHandler> logger)
    {
        _context = context;
        _zammadService = zammadService;
        _logger = logger;
    }

    public async Task<Result<SupportTicketDto>> Handle(UpdateTicketCommand request, CancellationToken ct)
    {
        var ticket = await _context.SupportTickets
            .Include(t => t.CustomerProfile)
            .FirstOrDefaultAsync(t => t.Id == request.Id, ct);

        if (ticket == null)
            return Result.Fail<SupportTicketDto>("Ticket not found");

        if (request.Data.Status.HasValue) ticket.Status = request.Data.Status.Value;
        if (request.Data.Priority.HasValue) ticket.Priority = request.Data.Priority.Value;
        if (request.Data.AssignedToUserId != null) ticket.AssignedToUserId = request.Data.AssignedToUserId;
        if (request.Data.AssignedToName != null) ticket.AssignedToName = request.Data.AssignedToName;
        if (request.Data.Resolution != null)
        {
            // SECURITY: Sanitize resolution text
            ticket.Resolution = InputSanitizer.SanitizePlainText(request.Data.Resolution, maxLength: 5000);
            ticket.ResolvedAt = DateTime.UtcNow;
            ticket.Status = TicketStatus.Resolved;
        }

        await _context.SaveChangesAsync(ct);

        // Sync status update to Zammad
        if (ticket.ZammadTicketId.HasValue && request.Data.Status.HasValue)
        {
            try
            {
                await _zammadService.UpdateTicketStatusAsync(
                    ticket.ZammadTicketId.Value, ticket.Status);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to sync status update for ticket {TicketNumber} to Zammad",
                    ticket.TicketNumber);
            }
        }

        return Result.Ok(GetTicketsHandler.ToDto(ticket));
    }
}

public class ResolveTicketHandler : IRequestHandler<ResolveTicketCommand, Result<SupportTicketDto>>
{
    private readonly CrmDbContext _context;
    private readonly ZammadService _zammadService;
    private readonly ILogger<ResolveTicketHandler> _logger;

    public ResolveTicketHandler(
        CrmDbContext context,
        ZammadService zammadService,
        ILogger<ResolveTicketHandler> logger)
    {
        _context = context;
        _zammadService = zammadService;
        _logger = logger;
    }

    public async Task<Result<SupportTicketDto>> Handle(ResolveTicketCommand request, CancellationToken ct)
    {
        var ticket = await _context.SupportTickets
            .Include(t => t.CustomerProfile)
            .FirstOrDefaultAsync(t => t.Id == request.Id, ct);

        if (ticket == null)
            return Result.Fail<SupportTicketDto>("Ticket not found");

        // SECURITY: Sanitize resolution text
        ticket.Resolution = InputSanitizer.SanitizePlainText(request.Resolution, maxLength: 5000);
        ticket.ResolvedAt = DateTime.UtcNow;
        ticket.Status = TicketStatus.Resolved;

        await _context.SaveChangesAsync(ct);

        // Sync resolution to Zammad
        if (ticket.ZammadTicketId.HasValue)
        {
            try
            {
                // The sanitized text, not request.Resolution - the raw value was what went to
                // Zammad while the cleaned one was stored, so the agent console showed input
                // the rest of the system had already decided was not safe to keep verbatim.
                await _zammadService.AddResolutionNoteAsync(
                    ticket.ZammadTicketId.Value, ticket.Resolution ?? string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to sync resolution for ticket {TicketNumber} to Zammad",
                    ticket.TicketNumber);
            }
        }

        return Result.Ok(GetTicketsHandler.ToDto(ticket));
    }
}

public class AddTicketCommentHandler : IRequestHandler<AddTicketCommentCommand, Result<Unit>>
{
    private readonly CrmDbContext _context;
    private readonly ZammadService _zammadService;
    private readonly ILogger<AddTicketCommentHandler> _logger;

    public AddTicketCommentHandler(
        CrmDbContext context,
        ZammadService zammadService,
        ILogger<AddTicketCommentHandler> logger)
    {
        _context = context;
        _zammadService = zammadService;
        _logger = logger;
    }

    public async Task<Result<Unit>> Handle(AddTicketCommentCommand request, CancellationToken ct)
    {
        var ticket = await _context.SupportTickets
            .Include(t => t.CustomerProfile)
            .FirstOrDefaultAsync(t => t.Id == request.Id, ct);

        if (ticket == null)
            return Result.Fail<Unit>("Ticket not found");

        if (ticket.Status == TicketStatus.Closed || ticket.Status == TicketStatus.Resolved)
            return Result.Fail<Unit>("Cannot add comments to a closed ticket");

        if (!ticket.ZammadTicketId.HasValue)
            return Result.Fail<Unit>("This ticket is not yet synced with support. Please try again later.");

        // Verify ownership: driver (UserId match) or customer (CustomerProfile links to user)
        var profile = ticket.CustomerProfileId.HasValue
            ? await _context.CustomerProfiles.FirstOrDefaultAsync(p => p.Id == ticket.CustomerProfileId.Value, ct)
            : null;
        var isDriver = ticket.UserId == request.UserId;
        var isCustomer = profile?.UserId == request.UserId;
        if (!isDriver && !isCustomer)
            return Result.Fail<Unit>("You do not have permission to add comments to this ticket");

        var sanitizedBody = InputSanitizer.SanitizePlainText(request.Body, maxLength: 2000);
        if (string.IsNullOrWhiteSpace(sanitizedBody))
            return Result.Fail<Unit>("Comment cannot be empty");

        var success = await _zammadService.AddArticleAsync(
            ticket.ZammadTicketId.Value,
            sanitizedBody,
            request.UserName ?? ticket.UserFullName,
            request.UserEmail ?? ticket.UserEmail);

        if (!success)
            return Result.Fail<Unit>("Failed to add comment. Please try again later.");

        return Result.Ok(Unit.Value);
    }
}

public class ProcessZammadWebhookHandler : IRequestHandler<ProcessZammadWebhookCommand, Unit>
{
    private readonly CrmDbContext _context;
    private readonly INotificationService _notifications;
    private readonly ILogger<ProcessZammadWebhookHandler> _logger;

    public ProcessZammadWebhookHandler(
        CrmDbContext context,
        INotificationService notifications,
        ILogger<ProcessZammadWebhookHandler> logger)
    {
        _context = context;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<Unit> Handle(ProcessZammadWebhookCommand request, CancellationToken ct)
    {
        var ticket = await _context.SupportTickets
            .FirstOrDefaultAsync(t => t.ZammadTicketId == request.ZammadTicketId, ct);

        if (ticket == null)
        {
            _logger.LogDebug("[ZAMMAD] Webhook for unknown ZammadId={ZammadId} (ticket not in our DB)", request.ZammadTicketId);
            return Unit.Value;
        }

        var anyUpdate = false;

        if (!string.IsNullOrWhiteSpace(request.State))
        {
            var newStatus = MapZammadStateToStatus(request.State);
            if (newStatus.HasValue && ticket.Status != newStatus.Value)
            {
                ticket.Status = newStatus.Value;
                if (newStatus.Value == TicketStatus.Closed || newStatus.Value == TicketStatus.Resolved)
                    ticket.ResolvedAt ??= DateTime.UtcNow;
                anyUpdate = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.OwnerName) && ticket.AssignedToName != request.OwnerName)
        {
            ticket.AssignedToName = request.OwnerName;
            anyUpdate = true;
        }

        var reply = SelectDeliverableReply(ticket, request.Article);
        if (reply != null)
        {
            ticket.LastZammadArticleId = reply.Id;
            anyUpdate = true;
        }

        if (anyUpdate)
        {
            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("[ZAMMAD] Webhook updated ticket {TicketNumber}: Status={Status}, AssignedTo={AssignedTo}",
                ticket.TicketNumber, ticket.Status, ticket.AssignedToName ?? "(none)");
        }

        // Pushed only after the high-water mark is committed. The reverse order would re-deliver
        // the same reply on every Zammad retry if the save failed, and a duplicate is the one
        // outcome the article id exists to prevent.
        if (reply != null)
            await PushReplyAsync(ticket, reply);

        return Unit.Value;
    }

    /// <summary>
    /// Decides whether an article should be delivered to the person who raised the ticket,
    /// returning it when so and null otherwise.
    /// </summary>
    private ZammadWebhookArticle? SelectDeliverableReply(SupportTicket ticket, ZammadWebhookArticle? article)
    {
        if (article == null)
            return null;

        // Checked first and deliberately so: an internal note is agent-to-agent, and showing one
        // to a driver is the worst thing this path can do. Everything below is a correctness
        // concern; this one is a disclosure concern.
        if (article.Internal)
        {
            _logger.LogDebug("[ZAMMAD] Article {ArticleId} on {TicketNumber} is internal; not delivered",
                article.Id, ticket.TicketNumber);
            return null;
        }

        // A "Customer" article is one we posted ourselves through AddArticleAsync when the user
        // commented from the app. Sending it back would show the author their own message a
        // second time, and would loop outright if the client ever echoed what it received.
        if (!string.Equals(article.Sender, "Agent", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("[ZAMMAD] Article {ArticleId} on {TicketNumber} has sender {Sender}; not delivered",
                article.Id, ticket.TicketNumber, article.Sender ?? "(null)");
            return null;
        }

        // Zammad retries deliveries and a trigger can fire twice for one article, so the same
        // reply legitimately arrives more than once. Ids are monotonic per instance.
        if (ticket.LastZammadArticleId >= article.Id)
        {
            _logger.LogDebug("[ZAMMAD] Article {ArticleId} on {TicketNumber} already delivered (high-water {Mark})",
                article.Id, ticket.TicketNumber, ticket.LastZammadArticleId);
            return null;
        }

        return article;
    }

    private async Task PushReplyAsync(SupportTicket ticket, ZammadWebhookArticle article)
    {
        if (string.IsNullOrWhiteSpace(ticket.UserId))
        {
            _logger.LogWarning(
                "[ZAMMAD] Cannot deliver article {ArticleId}: ticket {TicketNumber} has no UserId",
                article.Id, ticket.TicketNumber);
            return;
        }

        try
        {
            await _notifications.SendToUserAsync(ticket.UserId, "SupportTicketReply", new
            {
                TicketId = ticket.Id,
                ticket.TicketNumber,
                ArticleId = article.Id,
                article.Body,
                article.ContentType,
                article.From,
                CreatedAt = article.CreatedAt ?? DateTime.UtcNow
            });

            _logger.LogInformation("[ZAMMAD] Delivered article {ArticleId} on {TicketNumber} to user {UserId}",
                article.Id, ticket.TicketNumber, ticket.UserId);
        }
        catch (Exception ex)
        {
            // The reply is already in Zammad and the ticket screen re-fetches the full thread on
            // open, so a failed push costs immediacy, not the message. Rethrowing would make the
            // controller log an error and Zammad retry a delivery we have already recorded.
            _logger.LogWarning(ex, "[ZAMMAD] Failed to push article {ArticleId} on {TicketNumber}",
                article.Id, ticket.TicketNumber);
        }
    }

    /// <summary>
    /// Maps Zammad state names to our TicketStatus (reverse of ZammadService.MapStatus).
    /// </summary>
    private static TicketStatus? MapZammadStateToStatus(string state)
    {
        return state.ToLowerInvariant() switch
        {
            "new" => TicketStatus.Open,
            "open" => TicketStatus.InProgress,
            "pending reminder" => TicketStatus.WaitingCustomer,
            "pending close" => TicketStatus.Resolved,
            "closed" => TicketStatus.Closed,
            _ => null
        };
    }
}
