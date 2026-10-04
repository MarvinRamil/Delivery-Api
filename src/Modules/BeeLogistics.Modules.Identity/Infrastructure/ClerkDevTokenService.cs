using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Identity.Infrastructure;

/// <summary>Outcome of a dev token mint: either a usable session JWT or a reason it failed.</summary>
public sealed record ClerkDevTokenResult(bool Success, string? Jwt, string? ClerkUserId, string? SessionId, string? Error);

/// <summary>
/// DEV-ONLY helper that mints a real Clerk session JWT server-side so it can be pasted
/// into Swagger's "Authorize". Clerk normally issues session tokens in the browser/app;
/// this reproduces that flow with the Backend + Frontend APIs (see CLERK_MIGRATION_PLAN.md §2a):
///   1) Backend API: find the Clerk user by email.
///   2) Backend API: create a sign-in token (ticket) for that user.
///   3) Frontend API: exchange the ticket for a session (strategy: "ticket").
///   4) Backend API: mint a session token (JWT) from that session.
///
/// This is an auth bypass by design — the caller chooses which user to impersonate — so the
/// controller MUST gate it to Development / Swagger. Never expose it in production.
/// </summary>
public class ClerkDevTokenService
{
    private readonly HttpClient _http;
    private readonly ClerkSettings _settings;
    private readonly ILogger<ClerkDevTokenService> _logger;

    public ClerkDevTokenService(HttpClient http, IOptions<ClerkSettings> settings, ILogger<ClerkDevTokenService> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Mint a Clerk session JWT for the Clerk user that owns <paramref name="email"/>.
    /// When <paramref name="template"/> is set the token uses that JWT template (longer-lived,
    /// handy for Swagger); otherwise the default ~60s session token is returned.
    /// </summary>
    public async Task<ClerkDevTokenResult> MintTokenForEmailAsync(string email, string? template = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.SecretKey))
        {
            return new ClerkDevTokenResult(false, null, null, null, "Clerk:SecretKey is not configured.");
        }
        if (string.IsNullOrWhiteSpace(_settings.Authority))
        {
            return new ClerkDevTokenResult(false, null, null, null, "Clerk:Authority (Frontend API URL) is not configured.");
        }

        var userId = await FindUserIdByEmailAsync(email, ct);
        if (userId is null)
        {
            return new ClerkDevTokenResult(false, null, null, null, $"No Clerk user found for email '{email}'.");
        }

        var ticket = await CreateSignInTokenAsync(userId, ct);
        if (ticket is null)
        {
            return new ClerkDevTokenResult(false, null, userId, null, "Failed to create a Clerk sign-in token.");
        }

        var (sessionId, signInJwt) = await ExchangeTicketForSessionAsync(ticket, ct);
        if (sessionId is null)
        {
            return new ClerkDevTokenResult(false, null, userId, null, "Failed to exchange the sign-in ticket for a session.");
        }

        // Prefer a template token when one is configured (usually longer-lived, better for Swagger).
        // Otherwise use the default session token already returned by the sign-in exchange — the
        // Backend API's template-less /tokens route is not always available, so we don't rely on it.
        string? jwt = null;
        if (!string.IsNullOrWhiteSpace(template))
        {
            jwt = await MintSessionTokenAsync(sessionId, template, ct);
        }
        jwt ??= signInJwt;

        if (jwt is null)
        {
            return new ClerkDevTokenResult(false, null, userId, sessionId,
                "Session created but no token was returned (set Clerk:DevTokenTemplate to a valid JWT template, or check the sign-in response).");
        }

        return new ClerkDevTokenResult(true, jwt, userId, sessionId, null);
    }

    // 1) Backend API — resolve email → Clerk user id.
    private async Task<string?> FindUserIdByEmailAsync(string email, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.clerk.com/v1/users?email_address={Uri.EscapeDataString(email)}&limit=1");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.SecretKey);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("[ClerkDevToken] user lookup returned {Status} for {Email}", (int)resp.StatusCode, email);
            return null;
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        // Clerk returns an array of users.
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }
        return doc.RootElement[0].TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    // 2) Backend API — create a one-time sign-in token (ticket) for the user.
    private async Task<string?> CreateSignInTokenAsync(string userId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.clerk.com/v1/sign_in_tokens")
        {
            Content = JsonContent.Create(new { user_id = userId, expires_in_seconds = 300 })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.SecretKey);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("[ClerkDevToken] sign_in_tokens returned {Status}: {Body}",
                (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
            return null;
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
    }

    // 3) Frontend API — exchange the ticket for a completed sign-in. Returns the created
    //    session id AND the default session token (JWT) that the response already carries.
    private async Task<(string? SessionId, string? Jwt)> ExchangeTicketForSessionAsync(string ticket, CancellationToken ct)
    {
        var frontendApi = _settings.Authority.TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{frontendApi}/v1/client/sign_ins?_clerk_js_version=5")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["strategy"] = "ticket",
                ["ticket"] = ticket,
            })
        };
        // Frontend API is public (no secret key); it needs an Origin it recognises.
        req.Headers.TryAddWithoutValidation("Origin", frontendApi);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("[ClerkDevToken] client/sign_ins returned {Status}: {Body}", (int)resp.StatusCode, body);
            return (null, null);
        }
        using var doc = JsonDocument.Parse(body);
        // { "response": { "created_session_id": "sess_...", "status": "complete" },
        //   "client": { "sessions": [ { "id": "sess_...", "last_active_token": { "jwt": "..." } } ] } }
        var root = doc.RootElement;
        string? sessionId = null;
        if (root.TryGetProperty("response", out var response)
            && response.TryGetProperty("created_session_id", out var sid)
            && sid.ValueKind == JsonValueKind.String)
        {
            sessionId = sid.GetString();
        }
        if (sessionId is null)
        {
            _logger.LogWarning("[ClerkDevToken] sign-in did not complete: {Body}", body);
            return (null, null);
        }

        // Pull the session token the exchange already minted (matches created_session_id).
        var jwt = ExtractSessionJwt(root, sessionId);
        return (sessionId, jwt);
    }

    // The sign-in response embeds the client with its sessions; each carries last_active_token.jwt.
    private static string? ExtractSessionJwt(JsonElement root, string sessionId)
    {
        if (!root.TryGetProperty("client", out var client)
            || !client.TryGetProperty("sessions", out var sessions)
            || sessions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        JsonElement? match = null;
        foreach (var s in sessions.EnumerateArray())
        {
            var id = s.TryGetProperty("id", out var sid) ? sid.GetString() : null;
            if (id == sessionId) { match = s; break; }
            match ??= s;
        }
        if (match is { } session
            && session.TryGetProperty("last_active_token", out var tok)
            && tok.ValueKind == JsonValueKind.Object
            && tok.TryGetProperty("jwt", out var jwt))
        {
            return jwt.GetString();
        }
        return null;
    }

    // 4) Backend API — mint a session token (JWT) from the session, optionally via a JWT template.
    private async Task<string?> MintSessionTokenAsync(string sessionId, string? template, CancellationToken ct)
    {
        var url = string.IsNullOrWhiteSpace(template)
            ? $"https://api.clerk.com/v1/sessions/{sessionId}/tokens"
            : $"https://api.clerk.com/v1/sessions/{sessionId}/tokens/{Uri.EscapeDataString(template)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.SecretKey);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("[ClerkDevToken] sessions/{SessionId}/tokens returned {Status}: {Body}",
                sessionId, (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
            return null;
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("jwt", out var jwt) ? jwt.GetString() : null;
    }
}
