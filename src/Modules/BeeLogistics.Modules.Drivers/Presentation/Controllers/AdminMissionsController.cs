using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Drivers.Infrastructure;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Presentation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BeeLogistics.Modules.Drivers.Presentation.Controllers;

[Authorize(Policy = "Backoffice")]
[ApiController]
[Route("api/admin/missions")]
public class AdminMissionsController : BaseController
{
    private readonly DriversDbContext _driversDbContext;
    private readonly UserManager<ApplicationUser> _userManager;

    public AdminMissionsController(
        DriversDbContext driversDbContext,
        UserManager<ApplicationUser> userManager)
    {
        _driversDbContext = driversDbContext;
        _userManager = userManager;
    }

    public sealed record UpsertMissionRequest(
        string Title,
        string Description,
        decimal Reward,
        int Target,
        MissionType Type,
        DateTime ExpiresAt,
        bool IsActive = true
    );

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct = default)
    {
        var items = await _driversDbContext.GlobalMissions
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.Title,
                x.Description,
                x.Reward,
                x.Target,
                x.Type,
                x.ExpiresAt,
                x.IsActive,
                x.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(new { success = true, data = items });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpsertMissionRequest request, CancellationToken ct = default)
    {
        GlobalMission globalMission;
        try
        {
            globalMission = new GlobalMission(
                request.Title,
                request.Description,
                request.Reward,
                request.Target,
                request.Type,
                request.ExpiresAt);

            if (!request.IsActive)
            {
                globalMission.Update(
                    request.Title,
                    request.Description,
                    request.Reward,
                    request.Target,
                    request.Type,
                    request.ExpiresAt,
                    false);
            }
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }

        await _driversDbContext.GlobalMissions.AddAsync(globalMission, ct);

        // Global mission distribution: create concrete mission rows for all active drivers.
        var driverIds = await _userManager.Users
            .Where(u => u.Role == UserRoles.Driver && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(ct);

        foreach (var driverIdRaw in driverIds)
        {
            if (!Guid.TryParse(driverIdRaw, out var driverId))
                continue;

            var mission = new DriverMission(
                driverId,
                request.Title,
                request.Description,
                request.Reward,
                request.Target,
                request.Type,
                request.ExpiresAt,
                globalMission.Id);

            if (request.IsActive)
            {
                mission.Activate();
            }

            await _driversDbContext.DriverMissions.AddAsync(mission, ct);
        }

        await _driversDbContext.SaveChangesAsync(ct);
        return Ok(new { success = true, data = new { globalMission.Id, distributedTo = driverIds.Count } });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertMissionRequest request, CancellationToken ct = default)
    {
        var globalMission = await _driversDbContext.GlobalMissions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (globalMission == null)
            return NotFound(new { success = false, message = "Mission not found." });

        try
        {
            globalMission.Update(
                request.Title,
                request.Description,
                request.Reward,
                request.Target,
                request.Type,
                request.ExpiresAt,
                request.IsActive);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }

        var issuedMissions = await _driversDbContext.DriverMissions
            .Where(x => x.GlobalMissionId == id)
            .ToListAsync(ct);

        foreach (var mission in issuedMissions)
        {
            mission.SyncFromGlobalMission(
                request.Title,
                request.Description,
                request.Reward,
                request.Target,
                request.Type,
                request.ExpiresAt);

            if (!request.IsActive)
            {
                mission.Expire();
            }
        }

        await _driversDbContext.SaveChangesAsync(ct);
        return Ok(new { success = true });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var globalMission = await _driversDbContext.GlobalMissions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (globalMission == null)
            return NotFound(new { success = false, message = "Mission not found." });

        globalMission.SoftDelete(User.FindFirstValue(ClaimTypes.NameIdentifier));

        var issuedMissions = await _driversDbContext.DriverMissions
            .Where(x => x.GlobalMissionId == id)
            .ToListAsync(ct);

        foreach (var mission in issuedMissions)
        {
            mission.Expire();
        }

        await _driversDbContext.SaveChangesAsync(ct);
        return Ok(new { success = true });
    }
}
