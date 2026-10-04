using System.Net.Http.Headers;
using System.Text.Json;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Identity.Infrastructure;

/// <summary>Normalised Clerk user fields used to create/sync a local user.</summary>
public sealed record ClerkUserData(
    string ClerkUserId,
    string? Email,
    bool EmailVerified,
    string? Phone,
    bool PhoneVerified,
    string? FullName,
    string? Role,
    string? ReferralCode = null);

/// <summary>
/// Single source of truth for turning a Clerk identity into a local
/// <see cref="ApplicationUser"/>. Used by BOTH the Clerk webhook (background sync)
/// and just-in-time provisioning in /api/auth/me (so sign-in never depends on
/// webhook timing). The local record stays authoritative for role.
/// </summary>
public class ClerkUserProvisioningService
{
    private readonly HttpClient _http;
    private readonly ClerkSettings _settings;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<ClerkUserProvisioningService> _logger;
    private readonly IMediator _mediator;

    public ClerkUserProvisioningService(
        HttpClient http,
        IOptions<ClerkSettings> settings,
        UserManager<ApplicationUser> userManager,
        ILogger<ClerkUserProvisioningService> logger,
        IMediator mediator)
    {
        _http = http;
        _settings = settings.Value;
        _userManager = userManager;
        _logger = logger;
        _mediator = mediator;
    }

    /// <summary>
    /// Find-or-create the local user for a Clerk id. The expensive path (Clerk API
    /// call + create) runs ONLY when no local user with that ClerkUserId exists yet
    /// — so if the webhook already created it, this just returns the existing user.
    /// </summary>
    /// <param name="roleHint">
    /// Role for a NEW user, supplied by the calling app (the driver app sends the
    /// "X-Bee-Role: driver" header on /api/auth/me). Null/absent → Customer.
    /// </param>
    public async Task<ApplicationUser?> EnsureLocalUserAsync(string clerkUserId, string? roleHint = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clerkUserId))
        {
            return null;
        }

        // Gate: already provisioned (e.g. the webhook fired first) → no API call, no create.
        var existing = await _userManager.Users.FirstOrDefaultAsync(u => u.ClerkUserId == clerkUserId, ct);
        if (existing is not null)
        {
            return existing;
        }

        var data = await FetchClerkUserAsync(clerkUserId, ct);
        if (data is null)
        {
            return null;
        }
        // The role-aware sign-in path creates the user; pass the hint.
        return await UpsertAsync(data, roleHint, allowCreate: true, ct);
    }

    /// <summary>
    /// Create/link/sync the local user from Clerk user data. Idempotent on ClerkUserId.
    /// <paramref name="allowCreate"/> is false for the webhook (background sync) so that
    /// brand-new users are only ever created by the role-aware sign-in path
    /// (/api/auth/me), guaranteeing the correct role regardless of webhook timing.
    /// <paramref name="roleHint"/> sets the role for a newly created user (else Customer).
    /// </summary>
    public async Task<ApplicationUser?> UpsertAsync(ClerkUserData data, string? roleHint = null, bool allowCreate = true, CancellationToken ct = default)
    {
        // 1) Already linked by ClerkUserId.
        var user = await _userManager.Users.FirstOrDefaultAsync(u => u.ClerkUserId == data.ClerkUserId, ct);

        // 2) Existing (migrated/legacy) user matched by email but not yet linked → link it.
        if (user is null && !string.IsNullOrEmpty(data.Email))
        {
            user = await _userManager.FindByEmailAsync(data.Email);
            if (user is not null)
            {
                user.ClerkUserId = data.ClerkUserId;
            }
        }

        // 3) Brand new user. Only the sign-in path (JIT) creates — the webhook
        //    (allowCreate=false) skips, so role is always set by the role-aware path.
        if (user is null && !allowCreate)
        {
            _logger.LogDebug("[ClerkProvision] No local user for Clerk id {ClerkUserId}; skipping create (webhook/background sync).", data.ClerkUserId);
            return null;
        }
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = data.Email ?? data.ClerkUserId,
                Email = data.Email,
                FullName = string.IsNullOrWhiteSpace(data.FullName) ? (data.Email ?? data.ClerkUserId) : data.FullName,
                ClerkUserId = data.ClerkUserId,
                Role = NormalizeRole(roleHint ?? data.Role),
                IsActive = true,
                EmailConfirmed = data.EmailVerified,
                PhoneNumber = data.Phone,
                PhoneNumberConfirmed = data.PhoneVerified,
            };
            IdentityResult created;
            try
            {
                created = await _userManager.CreateAsync(user);
            }
            catch (DbUpdateException ex)
            {
                // The unique index caught what the validators could not. UserManager queries for a
                // duplicate email/username first, but a concurrent request can insert between that
                // check and ours - and the app fires several /api/auth/me calls on startup, so for
                // a first-time Clerk user that race is routine rather than exotic.
                //
                // Identity only reports the polite version of this failure (validators catching it)
                // as a failed IdentityResult; a database-level violation throws instead, which used
                // to escape as a 500. Treat it as the same outcome and fall into the re-resolve
                // below: the other request won, so their row is the one to return.
                //
                // Detach first. The failed entity is still tracked as Added, so the fallback's
                // UpdateAsync would call SaveChanges and re-attempt the very INSERT that just died.
                foreach (var entry in ex.Entries)
                    entry.State = EntityState.Detached;

                _logger.LogInformation(
                    "[ClerkProvision] Concurrent create for Clerk id {ClerkUserId} ({Constraint}); re-resolving the winner.",
                    data.ClerkUserId,
                    (ex.InnerException as Npgsql.PostgresException)?.ConstraintName ?? "unique constraint");

                created = IdentityResult.Failed(new IdentityError
                {
                    Code = "ConcurrentCreate",
                    Description = "Another request created this user first."
                });
            }

            if (!created.Succeeded)
            {
                // Likely a concurrent create (webhook + JIT racing) or the email/username
                // already exists (legacy row whose NormalizedEmail didn't match). Re-resolve
                // and link instead of failing.
                var fallback = await _userManager.Users.FirstOrDefaultAsync(u => u.ClerkUserId == data.ClerkUserId, ct);
                if (fallback is null && !string.IsNullOrEmpty(data.Email))
                {
                    fallback = await _userManager.FindByEmailAsync(data.Email);
                }
                if (fallback is not null)
                {
                    if (string.IsNullOrEmpty(fallback.ClerkUserId))
                    {
                        fallback.ClerkUserId = data.ClerkUserId;
                        await _userManager.UpdateAsync(fallback);
                    }
                    return fallback;
                }
                _logger.LogError("[ClerkProvision] Failed to create user for Clerk id {ClerkUserId}: {Errors}",
                    data.ClerkUserId, string.Join("; ", created.Errors.Select(e => e.Description)));
                return null;
            }
            _logger.LogInformation("[ClerkProvision] Created local user {UserId} for Clerk id {ClerkUserId}", user.Id, data.ClerkUserId);
            await ProcessReferralAsync(user, data.ReferralCode, ct);
            return user;
        }

        // Update contact fields (role is owned locally and NOT overwritten from Clerk).
        if (!string.IsNullOrEmpty(data.Email))
        {
            user.Email = data.Email;
            user.EmailConfirmed = data.EmailVerified;
        }
        if (!string.IsNullOrEmpty(data.Phone))
        {
            user.PhoneNumber = data.Phone;
            user.PhoneNumberConfirmed = data.PhoneVerified;
        }
        if (!string.IsNullOrWhiteSpace(data.FullName))
        {
            user.FullName = data.FullName;
        }
        await _userManager.UpdateAsync(user);
        _logger.LogInformation("[ClerkProvision] Synced local user {UserId} from Clerk id {ClerkUserId}", user.Id, data.ClerkUserId);
        return user;
    }

    /// <summary>Soft-delete (deactivate) the local user for a Clerk id.</summary>
    public async Task SoftDeleteAsync(string clerkUserId, CancellationToken ct = default)
    {
        var user = await _userManager.Users.FirstOrDefaultAsync(u => u.ClerkUserId == clerkUserId, ct);
        if (user is null)
        {
            return;
        }
        user.IsActive = false;
        await _userManager.UpdateAsync(user);
        _logger.LogInformation("[ClerkProvision] Soft-deleted local user {UserId} (Clerk id {ClerkUserId})", user.Id, clerkUserId);
    }

    /// <summary>
    /// Awards referral points when a newly provisioned user signed up with a referral code
    /// (passed by the app in Clerk unsafe_metadata). Failures never block provisioning.
    /// </summary>
    private async Task ProcessReferralAsync(ApplicationUser user, string? referralCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(referralCode))
        {
            return;
        }
        try
        {
            var referredUserType = user.Role == UserRoles.Driver ? "Driver" : "Customer";
            var result = await _mediator.Send(new ProcessReferralOnRegistrationCommand(
                referralCode.Trim().ToUpperInvariant(), user.Id, referredUserType), ct);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("[ClerkProvision] Referral processing failed for user {UserId}: {Error}", user.Id, result.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClerkProvision] Error processing referral code for user {UserId}", user.Id);
        }
    }

    // New users default to Customer unless registration explicitly tagged role=Driver
    // (driver app passes role in Clerk unsafe_metadata). Role stays locally authoritative after.
    private static string NormalizeRole(string? role)
        => string.Equals(role, UserRoles.Driver, StringComparison.OrdinalIgnoreCase)
            ? UserRoles.Driver
            : UserRoles.Customer;

    private async Task<ClerkUserData?> FetchClerkUserAsync(string clerkUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.SecretKey))
        {
            _logger.LogWarning("[ClerkProvision] SecretKey not configured; cannot fetch Clerk user {ClerkUserId}", clerkUserId);
            return null;
        }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.clerk.com/v1/users/{clerkUserId}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.SecretKey);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[ClerkProvision] Clerk API returned {Status} fetching user {ClerkUserId}", (int)resp.StatusCode, clerkUserId);
                return null;
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return ParseClerkUser(doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClerkProvision] Error fetching Clerk user {ClerkUserId}", clerkUserId);
            return null;
        }
    }

    /// <summary>Parse a Clerk user object (from the webhook payload's `data` or the Backend API).</summary>
    public static ClerkUserData? ParseClerkUser(JsonElement user)
    {
        var id = user.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        string? email = null;
        var emailVerified = false;
        if (user.TryGetProperty("email_addresses", out var emails) && emails.ValueKind == JsonValueKind.Array)
        {
            var primaryId = user.TryGetProperty("primary_email_address_id", out var pe) ? pe.GetString() : null;
            var chosen = PickPrimary(emails, primaryId);
            if (chosen is { } e)
            {
                email = e.TryGetProperty("email_address", out var ea) ? ea.GetString() : null;
                emailVerified = IsVerified(e);
            }
        }

        string? phone = null;
        var phoneVerified = false;
        if (user.TryGetProperty("phone_numbers", out var phones) && phones.ValueKind == JsonValueKind.Array)
        {
            var primaryId = user.TryGetProperty("primary_phone_number_id", out var pp) ? pp.GetString() : null;
            var chosen = PickPrimary(phones, primaryId);
            if (chosen is { } p)
            {
                phone = p.TryGetProperty("phone_number", out var pn) ? pn.GetString() : null;
                phoneVerified = IsVerified(p);
            }
        }

        // Phone collected at registration + role intent + referral code ride in unsafe_metadata.
        string? role = null;
        string? referralCode = null;
        if (user.TryGetProperty("unsafe_metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            if (string.IsNullOrWhiteSpace(phone)
                && meta.TryGetProperty("phoneNumber", out var mp) && mp.ValueKind == JsonValueKind.String)
            {
                phone = mp.GetString();
            }
            if (meta.TryGetProperty("role", out var mr) && mr.ValueKind == JsonValueKind.String)
            {
                role = mr.GetString();
            }
            if (meta.TryGetProperty("referralCode", out var mc) && mc.ValueKind == JsonValueKind.String)
            {
                referralCode = mc.GetString();
            }
        }

        var first = user.TryGetProperty("first_name", out var fn) ? fn.GetString() : null;
        var last = user.TryGetProperty("last_name", out var ln) ? ln.GetString() : null;
        var fullName = string.Join(" ", new[] { first, last }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return new ClerkUserData(
            id,
            email,
            emailVerified,
            string.IsNullOrWhiteSpace(phone) ? null : phone,
            phoneVerified,
            string.IsNullOrWhiteSpace(fullName) ? null : fullName,
            role,
            string.IsNullOrWhiteSpace(referralCode) ? null : referralCode);
    }

    private static JsonElement? PickPrimary(JsonElement array, string? primaryId)
    {
        JsonElement? first = null;
        foreach (var item in array.EnumerateArray())
        {
            first ??= item;
            if (primaryId != null && item.TryGetProperty("id", out var idEl) && idEl.GetString() == primaryId)
            {
                return item;
            }
        }
        return first;
    }

    private static bool IsVerified(JsonElement contact)
        => contact.TryGetProperty("verification", out var v)
           && v.ValueKind == JsonValueKind.Object
           && v.TryGetProperty("status", out var s)
           && string.Equals(s.GetString(), "verified", StringComparison.OrdinalIgnoreCase);
}
