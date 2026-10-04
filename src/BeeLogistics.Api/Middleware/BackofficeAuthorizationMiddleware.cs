using System.Security.Claims;

namespace BeeLogistics.Api.Middleware;

/// <summary>
/// Middleware to ensure only SuperAdmin and Admin can access backoffice endpoints
/// Validates JWT role claims server-side to prevent JWT tampering
/// </summary>
public class BackofficeAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<BackofficeAuthorizationMiddleware> _logger;
    private static readonly string[] BackofficeRoles = { "SuperAdmin", "Admin" };

    public BackofficeAuthorizationMiddleware(
        RequestDelegate next,
        ILogger<BackofficeAuthorizationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // List of backoffice-ONLY API endpoints (SuperAdmin/Admin only, not used by frontend)
        var path = context.Request.Path.Value ?? "";
        
        // Exclude public/auth endpoints and customer-specific endpoints
        var excludedPaths = new[]
        {
            "/api/auth/login",
            "/api/auth/register",
            "/api/auth/backoffice-login",
            "/api/bookings/my-bookings", // Customer's own bookings
            "/api/bookings/customer/", // Customer-specific bookings
            "/api/truck-types/active", // Public read access
            "/api/driver-applications/my-application", // Driver's own application (Driver role; controller authorizes)
        };

        if (excludedPaths.Any(ep => path.StartsWith(ep, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        var backofficeOnlyEndpoints = new[]
        {
            "/api/audit-logs",
            "/api/auth/create-backoffice-user",
            "/api/bookings/pending-assignment",
            "/api/bookings/classify-size",
            "/api/bookings/start-broadcast",
            "/api/customers",
            "/api/driver-applications", // Back-office: list, approve, reject, view documents
            "/api/giveaways", // Back-office giveaway CRUD; drivers can still call /enter via role checks
            "/api/campaigns", // Back-office campaign CRUD + active feed
            "/api/admin/missions", // Back-office global mission CRUD
            "/api/fraud", // Fraud detection: events and signals (backoffice only)
            "/api/integration", // S2S surface for the back-office backend (ServiceApiKey scheme)
            "/api/ledger", // Accounting/audit ledger (read-only, backoffice only)
            "/api/matrix/bookings", // Booking chat transcripts (#78) - back-office only.
                                    // NOTE: /api/matrix/session is deliberately NOT here; drivers and
                                    // customers call it to get their own chat credentials.
            "/api/truck-types", // Write operations only (GET /active is excluded above)
            // NOTE: /api/trucks removed - frontend users (Owner/Dispatcher) need access to their own company's trucks
            // TrucksController already filters by CompanyId, so users only see their own trucks
            // NOTE: /api/tickets and /api/chat/support removed - fleet owners need access to support
            // Controllers handle authorization per endpoint
        };

        var isBackofficeOnlyEndpoint = backofficeOnlyEndpoints.Any(ep => 
            path.StartsWith(ep, StringComparison.OrdinalIgnoreCase));

        // Driver-applications: allow Driver role to GET own application and POST (create); rest is backoffice-only
        if (path.StartsWith("/api/driver-applications", StringComparison.OrdinalIgnoreCase))
        {
            if (path.EndsWith("/my-application", StringComparison.OrdinalIgnoreCase) ||
                (path.TrimEnd('/').Equals("/api/driver-applications", StringComparison.OrdinalIgnoreCase) &&
                 context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)))
            {
                await _next(context);
                return;
            }
        }

        // Giveaways: allow driver entry endpoint while keeping admin CRUD backoffice-only
        if (path.StartsWith("/api/giveaways", StringComparison.OrdinalIgnoreCase))
        {
            if (context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                context.User.Identity?.IsAuthenticated == true)
            {
                await _next(context);
                return;
            }

            if (path.EndsWith("/enter", StringComparison.OrdinalIgnoreCase) &&
                context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }
        }

        // Campaigns: allow authenticated users to fetch active login popups
        if (path.Equals("/api/campaigns/active", StringComparison.OrdinalIgnoreCase) &&
            context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            context.User.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        // Skip check for endpoints that can be used by frontend users
        if (!isBackofficeOnlyEndpoint)
        {
            await _next(context);
            return;
        }

        // Check if user is authenticated
        if (!context.User.Identity?.IsAuthenticated ?? true)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "Unauthorized" });
            return;
        }

        // CRITICAL: Validate role from JWT claims (server-side validation prevents JWT tampering)
        // The JWT signature is validated by ASP.NET Core, but we need to verify the role claim
        var roleClaim = context.User.FindFirst("role")?.Value;
        var isBackofficeClaim = context.User.FindFirst("is_backoffice")?.Value == "true";
        var roleClaims = context.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();

        // Check if user has SuperAdmin or Admin role
        var hasBackofficeRole = BackofficeRoles.Contains(roleClaim) || 
                                roleClaims.Any(r => BackofficeRoles.Contains(r));

        // CRITICAL SECURITY: Both conditions must be true:
        // 1. User must have is_backoffice claim (set only by backoffice-login endpoint)
        // 2. User must have SuperAdmin or Admin role
        // This prevents JWT tampering - even if someone modifies the token, the signature won't match
        // and even if they somehow get a valid token, they can't add is_backoffice claim without the secret
        if (!isBackofficeClaim || !hasBackofficeRole)
        {
            _logger.LogWarning(
                "Unauthorized backoffice access attempt by user {UserId} with role {Role}, is_backoffice: {IsBackoffice}",
                context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                roleClaim,
                isBackofficeClaim);

            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new 
            { 
                success = false, 
                message = "Access denied. Backoffice is restricted to administrators only." 
            });
            return;
        }

        await _next(context);
    }
}

