using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeLogistics.Modules.CRM.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.CRM.Infrastructure;

public record ZammadArticle(string Body, string? Subject, string? From, string Sender, DateTime? CreatedAt, string ContentType, bool Internal);
public record ZammadTicketWithArticles(string Title, string State, List<ZammadArticle> Articles);

/// <summary>
/// Service for syncing tickets with the Zammad helpdesk system.
/// Uses the Zammad REST API v1 to create, update, and manage tickets.
/// Designed to be resilient — Zammad failures never block local ticket operations.
/// </summary>
public class ZammadService
{
    private readonly HttpClient _httpClient;
    private readonly ZammadSettings _settings;
    private readonly ILogger<ZammadService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public ZammadService(
        HttpClient httpClient,
        IOptions<ZammadSettings> settings,
        ILogger<ZammadService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;

        var baseUrl = !string.IsNullOrWhiteSpace(_settings.BaseUrl)
            ? _settings.BaseUrl.TrimEnd('/') + "/"
            : "http://10.10.10.91:9080/";

        _httpClient.BaseAddress = new Uri(baseUrl);
        var apiToken = _settings.ApiToken ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(apiToken))
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Token", $"token={apiToken}");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Checks if Zammad is reachable and the API token is valid.
    /// Call GET /api/v1/users/me - returns true if 200, false otherwise.
    /// </summary>
    public async Task<(bool Connected, string? Error)> CheckConnectionAsync(CancellationToken ct = default)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("zammad Integration disabled. Connection check skipped.");
            return (false, "Zammad integration is disabled");
        }

        try
        {
            var response = await _httpClient.GetAsync("api/v1/users/me", ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("zammad Connection check: connected (BaseUrl={BaseUrl})", _httpClient.BaseAddress);
                return (true, null);
            }
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("zammad Connection check: failed StatusCode={StatusCode} Body={Body}", response.StatusCode, body);
            return (false, $"HTTP {(int)response.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "zammad Connection check: failed - {Message}", ex.Message);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Finds an existing Zammad ticket by our ticket number, or null if there is none.
    /// </summary>
    /// <remarks>
    /// Guards the retry path. The inline create waits only 3 seconds; a slower call can still
    /// succeed after we have stopped listening, leaving a Zammad ticket whose id we never
    /// recorded. Retrying blindly would create a duplicate, so a retry asks this first.
    ///
    /// Returns null on any failure, including a search error - the caller treats "unknown"
    /// as "not found" and will create. That risks a duplicate in the narrow case where the
    /// search itself is broken, which is preferable to dropping a support ticket entirely.
    /// </remarks>
    public async Task<int?> FindTicketByNumberAsync(string ticketNumber, CancellationToken ct = default)
    {
        if (!_settings.Enabled || string.IsNullOrWhiteSpace(ticketNumber))
            return null;

        try
        {
            var query = Uri.EscapeDataString($"\"{ticketNumber}\"");
            var response = await _httpClient.GetAsync($"api/v1/tickets/search?query={query}&limit=5", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "zammad Ticket search for {TicketNumber} failed with status {StatusCode}",
                    ticketNumber, response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);

            // Zammad returns either an array of tickets or an assets/record_ids envelope
            // depending on the endpoint variant; handle the array form and fall back to ids.
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.TryGetProperty("number", out var number)
                        && string.Equals(number.GetString(), ticketNumber, StringComparison.OrdinalIgnoreCase)
                        && element.TryGetProperty("id", out var id))
                        return id.GetInt32();
                }
                // Title match is the fallback: our ticket number is not Zammad's own number.
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.TryGetProperty("note", out var note)
                        && (note.GetString() ?? "").Contains(ticketNumber, StringComparison.OrdinalIgnoreCase)
                        && element.TryGetProperty("id", out var id))
                        return id.GetInt32();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "zammad Ticket search for {TicketNumber} threw", ticketNumber);
            return null;
        }
    }

    /// <summary>
    /// Creates a ticket in Zammad corresponding to a local support ticket.
    /// Returns the Zammad ticket ID, or null if the operation fails.
    /// </summary>
    /// <param name="ticket">The local support ticket entity.</param>
    /// <param name="customerEmail">Email of the ticket creator (used for Zammad customer lookup/creation).</param>
    /// <param name="customerName">Display name of the ticket creator.</param>
    /// <param name="userType">Either "customer" or "driver" — determines the Zammad group.</param>
    /// <returns>The Zammad ticket ID, or null if creation failed.</returns>
    public async Task<int?> CreateTicketAsync(
        SupportTicket ticket,
        string customerEmail,
        string customerName,
        string userType)
    {
        if (!_settings.Enabled)
        {
            _logger.LogDebug("zammad Integration disabled, skipping ticket sync");
            return null;
        }

        try
        {
            var group = userType.Equals("driver", StringComparison.OrdinalIgnoreCase)
                ? _settings.DriverGroup
                : _settings.CustomerGroup;

            var zammadTicket = new
            {
                Title = ticket.Subject,
                Group = group,
                CustomerId = $"guess:{customerEmail}",
                Article = new
                {
                    Subject = ticket.Subject,
                    Body = FormatTicketBody(ticket, customerName, customerEmail, userType),
                    // Zammad defaults a missing content_type to text/plain, which is what we
                    // want - but it was implicit while the body carried HTML tags, so agents
                    // read the markup instead of the ticket. Stated outright now.
                    ContentType = "text/plain",
                    Type = "note",
                    Internal = false,
                    // Without this Zammad credits the article to the API token's owner, who is
                    // an agent. That misattributes every ticket to staff in the console, and
                    // ProcessZammadWebhookHandler treats Agent articles as replies worth pushing
                    // to the user - so the author would get their own complaint pushed back at
                    // them the moment they filed it.
                    Sender = "Customer"
                },
                Note = $"BeeApp Ticket: {ticket.TicketNumber} | Category: {ticket.Category} | Priority: {ticket.Priority}"
                       + (string.IsNullOrWhiteSpace(ticket.UserId) ? "" : $" | User ID: {ticket.UserId}"),
                Priority_id = MapPriority(ticket.Priority),
                Tags = $"bee-app,{userType},{ticket.Category.ToString().ToLowerInvariant()}"
            };

            var json = JsonSerializer.Serialize(zammadTicket, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation(
                "zammad Creating ticket for {TicketNumber} (user: {UserType}, email: {Email})",
                ticket.TicketNumber, userType, customerEmail);

            var response = await _httpClient.PostAsync("api/v1/tickets", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "zammad Ticket creation failed with status {StatusCode}: {Error}",
                    response.StatusCode, errorBody);
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            var zammadId = doc.RootElement.GetProperty("id").GetInt32();

            _logger.LogInformation(
                "zammad Ticket created successfully: ZammadId={ZammadId} for {TicketNumber}",
                zammadId, ticket.TicketNumber);

            return zammadId;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "zammad Failed to connect for ticket {TicketNumber}. Ticket saved locally but not synced",
                ticket.TicketNumber);
            return null;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex,
                "zammad Request timed out for ticket {TicketNumber}. Ticket saved locally but not synced",
                ticket.TicketNumber);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "zammad Unexpected error syncing ticket {TicketNumber}",
                ticket.TicketNumber);
            return null;
        }
    }

    /// <summary>
    /// Updates an existing Zammad ticket's status (e.g., when resolved or closed).
    /// </summary>
    public async Task<bool> UpdateTicketStatusAsync(int zammadTicketId, TicketStatus status)
    {
        if (!_settings.Enabled) return false;

        try
        {
            var zammadState = MapStatus(status);
            var payload = new { State = zammadState };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PutAsync($"api/v1/tickets/{zammadTicketId}", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "zammad Ticket status update failed for ZammadId={ZammadId}: {StatusCode} {Error}",
                    zammadTicketId, response.StatusCode, errorBody);
                return false;
            }

            _logger.LogInformation(
                "zammad Ticket status updated: ZammadId={ZammadId} → {Status}",
                zammadTicketId, zammadState);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "zammad Failed to update ticket status for ZammadId={ZammadId}",
                zammadTicketId);
            return false;
        }
    }

    /// <summary>
    /// Adds a resolution note to an existing Zammad ticket.
    /// </summary>
    public async Task<bool> AddResolutionNoteAsync(int zammadTicketId, string resolution)
    {
        if (!_settings.Enabled) return false;

        try
        {
            // Update state to closed and add article
            var payload = new
            {
                State = "closed",
                Article = new
                {
                    Subject = "Ticket Resolved",
                    Body = $"Resolution: {resolution}",
                    ContentType = "text/plain",
                    Type = "note",
                    Internal = false,
                    // Neither Customer nor Agent: this note is written by us when the backoffice
                    // resolves a ticket, so System is the only honest attribution. It also keeps
                    // the note out of the reply push, which is correct here - the resolution
                    // travels app-to-Zammad, and the user already has it on their own ticket
                    // (SupportTicket.Resolution, set in the same handler that calls this).
                    Sender = "System"
                }
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PutAsync($"api/v1/tickets/{zammadTicketId}", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "zammad Resolution note failed for ZammadId={ZammadId}: {Error}",
                    zammadTicketId, errorBody);
                return false;
            }

            _logger.LogInformation(
                "zammad Ticket resolved: ZammadId={ZammadId}", zammadTicketId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "zammad Failed to add resolution note to ticket ZammadId={ZammadId}",
                zammadTicketId);
            return false;
        }
    }

    /// <summary>
    /// Adds a customer/driver comment (article) to an existing Zammad ticket.
    /// POST /api/v1/ticket_articles with type=web, sender=Customer.
    /// </summary>
    public async Task<bool> AddArticleAsync(int zammadTicketId, string body, string? customerName = null, string? customerEmail = null)
    {
        if (!_settings.Enabled) return false;

        try
        {
            var displayName = !string.IsNullOrWhiteSpace(customerName) ? customerName : "Customer";
            var payload = new
            {
                TicketId = zammadTicketId,
                Subject = "Re: Ticket",
                Body = body,
                ContentType = "text/plain",
                Type = "web",
                Internal = false,
                Sender = "Customer",
                From = !string.IsNullOrWhiteSpace(customerEmail) ? $"{displayName} <{customerEmail}>" : displayName
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("api/v1/ticket_articles", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "zammad Add article failed for ZammadId={ZammadId}: {StatusCode} {Error}",
                    zammadTicketId, response.StatusCode, errorBody);
                return false;
            }

            _logger.LogInformation("zammad Comment added to ticket ZammadId={ZammadId}", zammadTicketId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "zammad Failed to add comment to ticket ZammadId={ZammadId}", zammadTicketId);
            return false;
        }
    }

    /// <summary>
    /// Fetches just the ticket state from Zammad (lightweight, for status sync).
    /// </summary>
    public async Task<string?> GetTicketStateAsync(int zammadTicketId, CancellationToken ct = default)
    {
        if (!_settings.Enabled) return null;
        try
        {
            var response = await _httpClient.GetAsync($"api/v1/tickets/{zammadTicketId}", ct);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "zammad Failed to fetch state for ticket {ZammadId}", zammadTicketId);
            return null;
        }
    }

    /// <summary>
    /// Fetches a ticket and its articles from Zammad.
    /// GET /api/v1/tickets/{id} and GET /api/v1/ticket_articles/by_ticket/{id}
    /// </summary>
    public async Task<ZammadTicketWithArticles?> GetTicketWithArticlesAsync(int zammadTicketId, CancellationToken ct = default)
    {
        if (!_settings.Enabled) return null;

        try
        {
            var ticketResponse = await _httpClient.GetAsync($"api/v1/tickets/{zammadTicketId}", ct);
            if (!ticketResponse.IsSuccessStatusCode) return null;

            var articlesResponse = await _httpClient.GetAsync($"api/v1/ticket_articles/by_ticket/{zammadTicketId}", ct);
            if (!articlesResponse.IsSuccessStatusCode) return null;

            var ticketJson = await ticketResponse.Content.ReadAsStringAsync(ct);
            var articlesJson = await articlesResponse.Content.ReadAsStringAsync(ct);

            using var ticketDoc = JsonDocument.Parse(ticketJson);
            using var articlesDoc = JsonDocument.Parse(articlesJson);

            var ticketRoot = ticketDoc.RootElement;
            var title = ticketRoot.TryGetProperty("title", out var t) ? t.GetString() : null;
            var state = ticketRoot.TryGetProperty("state", out var s) ? s.GetString() : null;

            var articles = new List<ZammadArticle>();
            foreach (var a in articlesDoc.RootElement.EnumerateArray())
            {
                var body = a.TryGetProperty("body", out var b) ? b.GetString() : null;
                var subject = a.TryGetProperty("subject", out var sub) ? sub.GetString() : null;
                var from = a.TryGetProperty("from", out var f) ? f.GetString() : null;
                var sender = a.TryGetProperty("sender", out var snd) ? snd.GetString() : null;
                var createdAt = a.TryGetProperty("created_at", out var ca) && DateTime.TryParse(ca.GetString(), out var dt) ? dt : (DateTime?)null;
                var contentType = a.TryGetProperty("content_type", out var ctProp) ? ctProp.GetString() : "text/plain";
                var isInternal = a.TryGetProperty("internal", out var i) && i.ValueKind == JsonValueKind.True;

                articles.Add(new ZammadArticle(body ?? "", subject, from, sender ?? "Unknown", createdAt, contentType, isInternal));
            }

            return new ZammadTicketWithArticles(title ?? "", state ?? "", articles.OrderBy(x => x.CreatedAt ?? DateTime.MinValue).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "zammad Failed to fetch ticket {ZammadId} from Zammad", zammadTicketId);
            return null;
        }
    }

    #region Private Helpers

    /// <summary>
    /// Maps our TicketPriority to Zammad's priority_id.
    /// Zammad defaults: 1 = low, 2 = normal, 3 = high.
    /// </summary>
    private static int MapPriority(TicketPriority priority) => priority switch
    {
        TicketPriority.Low => 1,
        TicketPriority.Normal => 2,
        TicketPriority.High => 3,
        TicketPriority.Urgent => 3,
        _ => 2
    };

    /// <summary>
    /// Maps our TicketStatus to Zammad state names.
    /// Zammad defaults: "new", "open", "pending reminder", "pending close", "closed".
    /// </summary>
    private static string MapStatus(TicketStatus status) => status switch
    {
        TicketStatus.Open => "new",
        TicketStatus.InProgress => "open",
        TicketStatus.WaitingCustomer => "pending reminder",
        TicketStatus.Resolved => "closed",
        TicketStatus.Closed => "closed",
        _ => "open"
    };

    /// <summary>
    /// Formats a human-readable ticket body for Zammad with metadata.
    /// </summary>
    /// <remarks>
    /// Plain text, and it must stay that way. This used to emit &lt;strong&gt; and &lt;br/&gt;
    /// while the article declared no content_type, so Zammad stored it as text/plain and agents
    /// read the tags rather than the ticket.
    ///
    /// The fix is plain text rather than declaring the article text/html, because
    /// <see cref="SupportTicket.Description"/> is user-supplied and interpolated straight into
    /// this string. Marking it as HTML would let a driver's free text render as markup in the
    /// agent console, and would leave every value below needing escaping. Formatting is not
    /// worth that; if it is ever wanted, escape first.
    /// </remarks>
    private static string FormatTicketBody(SupportTicket ticket, string customerName, string customerEmail, string userType)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BeeApp Support Ticket");
        sb.AppendLine($"Ticket #: {ticket.TicketNumber}");
        sb.AppendLine($"Submitted by: {customerName} ({userType})");
        // Email and user id go in the body as well as the customer record: Zammad indexes
        // article text, so an agent can find every ticket from one driver by searching either,
        // even when the customer record was matched to the wrong person.
        sb.AppendLine($"Email: {customerEmail}");
        if (!string.IsNullOrWhiteSpace(ticket.UserId))
            sb.AppendLine($"User ID: {ticket.UserId}");
        sb.AppendLine($"Category: {ticket.Category}");
        sb.AppendLine($"Priority: {ticket.Priority}");

        if (ticket.BookingId.HasValue)
            sb.AppendLine($"Related Booking: {ticket.BookingId}");

        if (!string.IsNullOrWhiteSpace(ticket.Description))
        {
            sb.AppendLine();
            sb.AppendLine("Description:");
            sb.AppendLine(ticket.Description);
        }

        return sb.ToString();
    }

    #endregion
}
