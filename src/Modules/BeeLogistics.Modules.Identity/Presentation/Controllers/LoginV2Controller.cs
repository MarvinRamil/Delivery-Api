using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace BeeLogistics.Modules.Identity.Presentation.Controllers;

/// <summary>
/// Development/Swagger-only sign-in helper. Given an email, it mints a session JWT for the
/// matching identity provider account and ensures the local user exists with the requested
/// role, returning a token ready to paste into Swagger's "Authorize".
///
/// Hard-gated: returns 404 outside Development (unless Swagger is explicitly enabled), so it
/// is invisible and uncallable in production. Named neutrally on purpose.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/auth")]
public class LoginV2Controller : ControllerBase
{
    private readonly ClerkDevTokenService _devToken;
    private readonly ClerkUserProvisioningService _provisioning;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _env;

    public LoginV2Controller(
        ClerkDevTokenService devToken,
        ClerkUserProvisioningService provisioning,
        UserManager<ApplicationUser> userManager,
        IMemoryCache cache,
        IConfiguration configuration,
        IWebHostEnvironment env)
    {
        _devToken = devToken;
        _provisioning = provisioning;
        _userManager = userManager;
        _cache = cache;
        _configuration = configuration;
        _env = env;
    }

    /// <param name="Email">Email of an existing Clerk user to sign in as.</param>
    /// <param name="Role">
    /// Optional role to give the local user (e.g. "Admin", "SuperAdmin", "Driver", "Customer").
    /// Defaults to "Customer". Dev-only: any role is honoured here so you can get an admin token.
    /// </param>
    public record LoginV2Request(string Email, string? Role = null);

    [HttpPost("loginv2")]
    public async Task<IActionResult> LoginV2([FromBody] LoginV2Request request)
    {
        // Gate: explicit opt-in only. This mints admin-capable tokens, so it must NOT key off
        // the environment name — the deployed dev/staging hosts run as Development/Staging and
        // are internet-facing. Set DevTools__Enabled=true ONLY on a local machine. 404 otherwise.
        var enabled = _configuration.GetValue<bool>("DevTools:Enabled", false);
        if (!enabled)
        {
            return NotFound();
        }
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new { message = "Email is required." });
        }

        var ct = HttpContext.RequestAborted;
        var template = _configuration["Clerk:DevTokenTemplate"]; // optional longer-lived JWT template

        // 1) Mint a session JWT for the identity-provider account behind this email.
        var result = await _devToken.MintTokenForEmailAsync(request.Email.Trim(), template, ct);
        if (!result.Success || result.Jwt is null || result.ClerkUserId is null)
        {
            return BadRequest(new { message = result.Error ?? "Failed to mint a token." });
        }

        // 2) Ensure the local user exists (so role-gated endpoints work immediately, not just
        //    after a /api/auth/me warm-up). EnsureLocalUserAsync only creates/links Driver or
        //    Customer, so for any other requested role we set it explicitly below (dev-only).
        var user = await _provisioning.EnsureLocalUserAsync(result.ClerkUserId, request.Role, ct);
        if (user is null)
        {
            return BadRequest(new { message = "Token minted, but the local user could not be provisioned." });
        }

        if (!string.IsNullOrWhiteSpace(request.Role) && !string.Equals(user.Role, request.Role, StringComparison.OrdinalIgnoreCase))
        {
            user.Role = request.Role;
            await _userManager.UpdateAsync(user);
            // The role claims transform caches role for 60s keyed by Clerk id; drop it so the
            // new role is reflected on the very next request with this token.
            _cache.Remove($"clerk-role:{result.ClerkUserId}");
        }

        return Ok(new
        {
            token = result.Jwt,
            role = user.Role,
            localUserId = user.Id,
            clerkUserId = result.ClerkUserId,
            sessionId = result.SessionId,
            note = "Paste 'token' into Swagger Authorize (Bearer).",
        });
    }
}
