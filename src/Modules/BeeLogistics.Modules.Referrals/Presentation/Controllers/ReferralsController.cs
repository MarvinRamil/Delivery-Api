using BeeLogistics.Modules.Referrals.Application.DTOs;
using BeeLogistics.Modules.Referrals.Application.Handlers;
using BeeLogistics.Modules.Referrals.Domain;
using BeeLogistics.Modules.Referrals.Infrastructure.Services;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BeeLogistics.Modules.Referrals.Presentation.Controllers;

[Authorize]
[ApiController]
[Route("api/referrals")]
public class ReferralsController : BaseController
{
    private readonly IMediator _mediator;
    private readonly IQrCodeService _qrCodeService;

    public ReferralsController(IMediator mediator, IQrCodeService qrCodeService)
    {
        _mediator = mediator;
        _qrCodeService = qrCodeService;
    }

    /// <summary>
    /// SECURITY: referral data is owner-only. The {userId} route segment must match the
    /// authenticated user unless the caller is a back-office admin.
    /// </summary>
    private bool IsOwnerOrAdmin(Guid userId)
    {
        if (User.IsInRole(UserRoles.SuperAdmin))
            return true;

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(currentUserId, out var callerId) && callerId == userId;
    }

    /// <summary>Referral codes are typed by the owner's role: drivers refer as Driver, everyone else as Customer.</summary>
    private ReferralUserType CallerUserType()
        => User.IsInRole(UserRoles.Driver) ? ReferralUserType.Driver : ReferralUserType.Customer;

    [HttpGet("code/{userId}")]
    public async Task<IActionResult> GetReferralCode(Guid userId, CancellationToken ct = default)
    {
        if (!IsOwnerOrAdmin(userId))
            return Forbid();

        var query = new GetReferralCodeQuery(userId, CallerUserType());
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    [HttpPost("code/{userId}")]
    public async Task<IActionResult> CreateReferralCode(Guid userId, CancellationToken ct = default)
    {
        if (!IsOwnerOrAdmin(userId))
            return Forbid();

        var command = new CreateReferralCodeCommand(userId, CallerUserType());
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    [HttpGet("code/{userId}/qr")]
    public async Task<IActionResult> GetReferralQrCode(Guid userId, CancellationToken ct = default)
    {
        if (!IsOwnerOrAdmin(userId))
            return Forbid();

        var query = new GetReferralCodeQuery(userId, CallerUserType());
        var result = await _mediator.Send(query, ct);
        
        if (!result.IsSuccess || result.Value == null)
        {
            return FromResult(result);
        }

        var qrCodeBase64 = _qrCodeService.GenerateQrCodeBase64(result.Value.ReferralLink);
        
        return Ok(new { qrCode = qrCodeBase64, referralLink = result.Value.ReferralLink });
    }

    [HttpGet("{userId}/referrals")]
    public async Task<IActionResult> GetReferrals(
        Guid userId,
        [FromQuery] ReferralStatus? status = null,
        CancellationToken ct = default)
    {
        if (!IsOwnerOrAdmin(userId))
            return Forbid();

        var query = new GetReferralsQuery(userId, status);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    [HttpGet("{userId}/points")]
    public async Task<IActionResult> GetUserPoints(Guid userId, CancellationToken ct = default)
    {
        if (!IsOwnerOrAdmin(userId))
            return Forbid();

        var query = new GetUserPointsQuery(userId);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    [HttpGet("{userId}/points/transactions")]
    public async Task<IActionResult> GetPointsTransactions(
        Guid userId,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken ct = default)
    {
        if (!IsOwnerOrAdmin(userId))
            return Forbid();

        var query = new GetPointsTransactionsQuery(userId, startDate, endDate);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    [HttpPost("{userId}/points/redeem")]
    public async Task<IActionResult> RedeemPoints(
        Guid userId,
        [FromBody] RedeemPointsDto dto,
        CancellationToken ct = default)
    {
        if (!IsOwnerOrAdmin(userId))
            return Forbid();

        var command = new RedeemPointsCommand(userId, dto.Points, dto.Description);
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }
}

