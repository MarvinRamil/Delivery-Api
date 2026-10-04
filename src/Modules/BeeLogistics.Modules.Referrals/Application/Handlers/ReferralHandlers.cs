using BeeLogistics.Modules.Referrals.Application.DTOs;
using BeeLogistics.Modules.Referrals.Application.Interfaces;
using BeeLogistics.Modules.Referrals.Domain;
using BeeLogistics.Modules.Referrals.Infrastructure.Services;
using BeeLogistics.Shared.Abstractions;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Referrals.Application.Handlers;

// Queries
public record GetReferralCodeQuery(Guid UserId, ReferralUserType UserType) : IRequest<Result<ReferralCodeDto>>;
public record GetReferralsQuery(Guid UserId, ReferralStatus? Status = null) : IRequest<Result<IReadOnlyList<ReferralDto>>>;
public record GetUserPointsQuery(Guid UserId) : IRequest<Result<UserPointsDto>>;
public record GetPointsTransactionsQuery(Guid UserId, DateTime? StartDate = null, DateTime? EndDate = null) 
    : IRequest<Result<IReadOnlyList<PointsTransactionDto>>>;

// Commands
public record CreateReferralCodeCommand(Guid UserId, ReferralUserType UserType) : IRequest<Result<ReferralCodeDto>>;
public record RedeemPointsCommand(Guid UserId, int Points, string Description) : IRequest<Result<UserPointsDto>>;

// Handlers
public class GetReferralCodeQueryHandler : IRequestHandler<GetReferralCodeQuery, Result<ReferralCodeDto>>
{
    private readonly IReferralRepository _repository;
    private readonly IQrCodeService _qrCodeService;
    private readonly IConfiguration _configuration;

    public GetReferralCodeQueryHandler(IReferralRepository repository, IQrCodeService qrCodeService, IConfiguration configuration)
    {
        _repository = repository;
        _qrCodeService = qrCodeService;
        _configuration = configuration;
    }

    public async Task<Result<ReferralCodeDto>> Handle(GetReferralCodeQuery request, CancellationToken ct)
    {
        var referralCode = await _repository.GetReferralCodeByUserIdAsync(request.UserId, ct);
        
        if (referralCode == null)
        {
            // Create referral code if it doesn't exist
            var baseUrl = _configuration["Referrals:BaseUrl"] ?? "https://mybeeapp.com";
            var newCode = ReferralCode.Create(request.UserId, request.UserType, baseUrl);
            try
            {
                referralCode = await _repository.CreateReferralCodeAsync(newCode, ct);
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                // Unique index on UserId: a concurrent request created the code first — use that one.
                referralCode = await _repository.GetReferralCodeByUserIdAsync(request.UserId, ct);
                if (referralCode == null) throw;
            }
        }

        var dto = new ReferralCodeDto(
            referralCode.Id,
            referralCode.UserId,
            referralCode.Code,
            referralCode.ReferralLink,
            referralCode.CreatedAt
        );

        return Result.Ok(dto);
    }
}

public class GetReferralsQueryHandler : IRequestHandler<GetReferralsQuery, Result<IReadOnlyList<ReferralDto>>>
{
    private readonly IReferralRepository _repository;

    public GetReferralsQueryHandler(IReferralRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<ReferralDto>>> Handle(GetReferralsQuery request, CancellationToken ct)
    {
        var referrals = await _repository.GetReferralsByReferrerIdAsync(request.UserId, request.Status, ct);

        var dtos = referrals.Select(r => new ReferralDto(
            r.Id,
            r.ReferrerId,
            r.ReferredUserId,
            r.Status,
            r.ReferredAt,
            r.CompletedAt
        )).ToList();

        return Result.Ok<IReadOnlyList<ReferralDto>>(dtos);
    }
}

public class GetUserPointsQueryHandler : IRequestHandler<GetUserPointsQuery, Result<UserPointsDto>>
{
    private readonly IReferralRepository _repository;

    public GetUserPointsQueryHandler(IReferralRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<UserPointsDto>> Handle(GetUserPointsQuery request, CancellationToken ct)
    {
        var userPoints = await _repository.GetUserPointsByUserIdAsync(request.UserId, ct);
        
        if (userPoints == null)
        {
            // Create UserPoints if it doesn't exist
            var newUserPoints = new UserPoints(request.UserId);
            userPoints = await _repository.CreateUserPointsAsync(newUserPoints, ct);
        }

        var dto = new UserPointsDto(
            userPoints.Id,
            userPoints.UserId,
            userPoints.TotalPoints,
            userPoints.AvailablePoints,
            userPoints.PendingPoints,
            userPoints.LastUpdatedAt
        );

        return Result.Ok(dto);
    }
}

public class GetPointsTransactionsQueryHandler : IRequestHandler<GetPointsTransactionsQuery, Result<IReadOnlyList<PointsTransactionDto>>>
{
    private readonly IReferralRepository _repository;

    public GetPointsTransactionsQueryHandler(IReferralRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<PointsTransactionDto>>> Handle(GetPointsTransactionsQuery request, CancellationToken ct)
    {
        var transactions = await _repository.GetPointsTransactionsByUserIdAsync(
            request.UserId,
            request.StartDate,
            request.EndDate,
            ct
        );

        var dtos = transactions.Select(t => new PointsTransactionDto(
            t.Id,
            t.UserId,
            t.Type,
            t.Points,
            t.BalanceAfter,
            t.Description,
            t.RelatedReferralId,
            t.TransactionDate
        )).ToList();

        return Result.Ok<IReadOnlyList<PointsTransactionDto>>(dtos);
    }
}

public class CreateReferralCodeCommandHandler : IRequestHandler<CreateReferralCodeCommand, Result<ReferralCodeDto>>
{
    private readonly IReferralRepository _repository;
    private readonly IConfiguration _configuration;

    public CreateReferralCodeCommandHandler(IReferralRepository repository, IConfiguration configuration)
    {
        _repository = repository;
        _configuration = configuration;
    }

    public async Task<Result<ReferralCodeDto>> Handle(CreateReferralCodeCommand request, CancellationToken ct)
    {
        // Check if referral code already exists
        var existing = await _repository.GetReferralCodeByUserIdAsync(request.UserId, ct);
        if (existing != null)
        {
            var dto = new ReferralCodeDto(
                existing.Id,
                existing.UserId,
                existing.Code,
                existing.ReferralLink,
                existing.CreatedAt
            );
            return Result.Ok(dto);
        }

        var baseUrl = _configuration["Referrals:BaseUrl"] ?? "https://mybeeapp.com";
        var referralCode = ReferralCode.Create(request.UserId, request.UserType, baseUrl);
        try
        {
            referralCode = await _repository.CreateReferralCodeAsync(referralCode, ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            // Unique index on UserId: a concurrent request created the code first — use that one.
            var raced = await _repository.GetReferralCodeByUserIdAsync(request.UserId, ct);
            if (raced == null) throw;
            referralCode = raced;
        }

        var newDto = new ReferralCodeDto(
            referralCode.Id,
            referralCode.UserId,
            referralCode.Code,
            referralCode.ReferralLink,
            referralCode.CreatedAt
        );

        return Result.Ok(newDto);
    }
}

public class RedeemPointsCommandHandler : IRequestHandler<RedeemPointsCommand, Result<UserPointsDto>>
{
    private readonly IReferralRepository _repository;
    private readonly ILogger<RedeemPointsCommandHandler> _logger;

    public RedeemPointsCommandHandler(IReferralRepository repository, ILogger<RedeemPointsCommandHandler> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<Result<UserPointsDto>> Handle(RedeemPointsCommand request, CancellationToken ct)
    {
        var userPoints = await _repository.GetUserPointsByUserIdAsync(request.UserId, ct);
        if (userPoints == null)
        {
            return Result.NotFound<UserPointsDto>("User points not found");
        }

        try
        {
            userPoints.RedeemPoints(request.Points);

            // Create transaction
            var transaction = new PointsTransaction(
                request.UserId,
                PointsTransactionType.Redeemed,
                -request.Points, // Negative for redemption
                userPoints.AvailablePoints,
                request.Description,
                null
            );

            await _repository.CreatePointsTransactionAsync(transaction, ct);
            await _repository.UpdateUserPointsAsync(userPoints, ct);

            var dto = new UserPointsDto(
                userPoints.Id,
                userPoints.UserId,
                userPoints.TotalPoints,
                userPoints.AvailablePoints,
                userPoints.PendingPoints,
                userPoints.LastUpdatedAt
            );

            return Result.Ok(dto);
        }
        catch (InvalidOperationException ex)
        {
            // Business rule violation (e.g., insufficient points)
            _logger.LogWarning("Points redemption failed for user {UserId}: {Message}", request.UserId, ex.Message);
            return Result.Fail<UserPointsDto>(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error redeeming points for user {UserId}", request.UserId);
            return Result.Fail<UserPointsDto>("An error occurred while redeeming points. Please try again.");
        }
    }
}

