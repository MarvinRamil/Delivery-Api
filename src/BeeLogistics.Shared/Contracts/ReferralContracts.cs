using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Shared contract for processing referrals during registration.
/// This avoids circular dependencies between Identity and Referrals modules.
/// </summary>
public record ProcessReferralOnRegistrationCommand(
    string ReferralCode,
    string ReferredUserId,
    string ReferredUserType // "Driver" or "Customer"
) : IRequest<Result>;

