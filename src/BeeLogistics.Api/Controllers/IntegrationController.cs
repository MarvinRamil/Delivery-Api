using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BeeLogistics.Api.Handlers;
using BeeLogistics.Shared.Infrastructure.Security;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace BeeLogistics.Api.Controllers;

/// <summary>
/// S2S surface for the back-office backend (ServiceApiKey scheme only).
/// </summary>
[Route("api/integration")]
[Authorize(AuthenticationSchemes = ServiceApiKeyDefaults.SchemeName, Policy = "Backoffice")]
public class IntegrationController : BaseController
{
    private readonly IMediator _mediator;
    private readonly BeeLogistics.Modules.Drivers.Infrastructure.DriversDbContext _driversDb;
    private readonly BeeLogistics.Modules.Giveaways.Infrastructure.GiveawaysDbContext _giveawaysDb;
    private readonly BeeLogistics.Modules.Offers.Infrastructure.OffersDbContext _offersDb;
    private readonly IConfiguration _configuration;

    public IntegrationController(
        IMediator mediator,
        BeeLogistics.Modules.Drivers.Infrastructure.DriversDbContext driversDb,
        BeeLogistics.Modules.Giveaways.Infrastructure.GiveawaysDbContext giveawaysDb,
        BeeLogistics.Modules.Offers.Infrastructure.OffersDbContext offersDb,
        IConfiguration configuration)
    {
        _mediator = mediator;
        _driversDb = driversDb;
        _giveawaysDb = giveawaysDb;
        _offersDb = offersDb;
        _configuration = configuration;
    }

    /// <summary>
    /// Aggregate stats for the back-office dashboard: the existing booking
    /// dashboard counts plus admin-relevant totals across modules.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        CancellationToken ct = default)
    {
        var bookingStats = await _mediator.Send(new GetDashboardStatsQuery(dateFrom, dateTo), ct);
        if (!bookingStats.IsSuccess)
            return FromResult(bookingStats);

        var now = DateTime.UtcNow;
        var pendingDriverApplications = await _driversDb.DriverApplications
            .CountAsync(a => a.Status == BeeLogistics.Modules.Drivers.Domain.DriverApplicationStatus.Pending, ct);
        var activeGlobalMissions = await _driversDb.GlobalMissions
            .CountAsync(m => m.IsActive && m.ExpiresAt > now, ct);
        var activeGiveaways = await _giveawaysDb.Giveaways
            .CountAsync(g => g.IsActive && g.EndDate >= now, ct);
        var activeCampaigns = await _giveawaysDb.Campaigns
            .CountAsync(c => c.IsActive && c.EndDate >= now, ct);
        var activeOffers = await _offersDb.Offers
            .CountAsync(o => o.IsActive && o.StartsAt <= now && o.EndsAt >= now, ct);

        return Ok(new
        {
            success = true,
            data = new
            {
                bookings = bookingStats.Value,
                pendingDriverApplications,
                activeGlobalMissions,
                activeGiveaways,
                activeCampaigns,
                activeOffers,
            }
        });
    }

    public record HubTokenResponse(string Token, DateTime Expiration);

    /// <summary>
    /// Issues a short-lived bee-signed JWT so the back-office UI can connect to
    /// the SignalR hubs hosted here after admin auth moved to the back-office
    /// backend. Carries the same claim shape as backoffice-login tokens.
    /// </summary>
    [HttpPost("hub-token")]
    public IActionResult CreateHubToken()
    {
        var jwtSettings = _configuration.GetSection("JwtSettings");
        var secret = jwtSettings["Secret"];
        if (string.IsNullOrWhiteSpace(secret))
            return StatusCode(500, new { success = false, message = "JWT not configured" });

        var serviceUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "backoffice-service";
        var actingAdmin = User.FindFirstValue("acting_admin");

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, serviceUserId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("role", "SuperAdmin"),
            new(ClaimTypes.Role, "SuperAdmin"),
            new("is_backoffice", "true"),
            new("auth_type", "hub_token"),
        };
        if (!string.IsNullOrWhiteSpace(actingAdmin))
        {
            claims.Add(new Claim("acting_admin", actingAdmin));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddMinutes(60);

        var token = new JwtSecurityToken(
            issuer: jwtSettings["Issuer"],
            audience: jwtSettings["Audience"],
            claims: claims,
            expires: expires,
            signingCredentials: creds);

        return Ok(new
        {
            success = true,
            data = new HubTokenResponse(new JwtSecurityTokenHandler().WriteToken(token), expires)
        });
    }
}
