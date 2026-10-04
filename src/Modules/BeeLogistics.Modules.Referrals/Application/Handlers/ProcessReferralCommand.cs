using BeeLogistics.Modules.Referrals.Application.Interfaces;
using BeeLogistics.Modules.Referrals.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MediatR;

namespace BeeLogistics.Modules.Referrals.Application.Handlers;

// Handler for shared contract - processes referral when a new user registers
public class ProcessReferralOnRegistrationCommandHandler : IRequestHandler<ProcessReferralOnRegistrationCommand, Result>
{
    private readonly IReferralRepository _repository;

    public ProcessReferralOnRegistrationCommandHandler(IReferralRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result> Handle(ProcessReferralOnRegistrationCommand request, CancellationToken ct)
    {
        // Parse user ID
        if (!Guid.TryParse(request.ReferredUserId, out var referredUserId))
        {
            return Result.Fail("Invalid user ID");
        }

        // Parse user type
        if (!Enum.TryParse<ReferralUserType>(request.ReferredUserType, out var referredUserType))
        {
            return Result.Fail("Invalid user type");
        }

        // Check if user was already referred
        var existingReferral = await _repository.GetReferralByReferredUserIdAsync(referredUserId, ct);
        if (existingReferral != null)
        {
            return Result.Fail("User has already been referred");
        }

        // Find referral code
        var referralCode = await _repository.GetReferralCodeByCodeAsync(request.ReferralCode, ct);
        if (referralCode == null)
        {
            return Result.Fail("Invalid referral code");
        }

        // Don't allow self-referral
        if (referralCode.UserId == referredUserId)
        {
            return Result.Fail("Cannot use your own referral code");
        }

        // Determine referrer type based on referral code
        var referrerType = referralCode.UserType;

        // Create referral record
        var referral = new Referral(
            referralCode.Id,
            referralCode.UserId,
            referredUserId,
            referrerType,
            referredUserType
        );

        // Initialize UserPoints for the referrer (if not exists)
        var referrerPoints = await _repository.GetUserPointsByUserIdAsync(referralCode.UserId, ct);
        if (referrerPoints == null)
        {
            referrerPoints = new UserPoints(referralCode.UserId);
            await _repository.CreateUserPointsAsync(referrerPoints, ct);
        }

        // Add pending points (10 points per referral)
        const decimal referralPoints = 10m;
        referrerPoints.AddPendingPoints(referralPoints);

        // Create points transaction
        var transaction = new PointsTransaction(
            referralCode.UserId,
            PointsTransactionType.ReferralPending,
            referralPoints,
            referrerPoints.AvailablePoints + referrerPoints.PendingPoints,
            $"Referral: {request.ReferredUserType} registered",
            null
        );

        await _repository.CreateReferralAsync(referral, ct);
        await _repository.CreatePointsTransactionAsync(transaction, ct);
        await _repository.UpdateUserPointsAsync(referrerPoints, ct);

        return Result.Ok();
    }
}

