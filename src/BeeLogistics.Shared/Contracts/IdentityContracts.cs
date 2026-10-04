using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Shared.Contracts;

/// <summary>Contact details for a user, resolved from Identity by user id.</summary>
public record UserContactInfo(string UserId, string? Email, string? FullName);

/// <summary>
/// Resolves a user's email and display name from their Identity user id.
/// </summary>
/// <remarks>
/// Exists so modules that do not reference Identity can still attribute records to a real
/// person rather than falling back to a placeholder. Added for CRM ticket creation, where
/// identity was read from JWT claims alone and drivers whose claims did not resolve were
/// filed under a shared "unknown@example.com" customer.
/// </remarks>
public record GetUserContactQuery(string UserId) : IRequest<Result<UserContactInfo>>;

/// <summary>
/// Resolves the vehicle type a driver is registered under, from their Identity user id.
/// Returns <c>null</c> when the user exists but has no vehicle type on file.
/// </summary>
/// <remarks>
/// Exists because a driver's vehicle type is recorded in two places and only one of them is
/// populated for most drivers: <c>DriverApplications.VehicleType</c> is written by the
/// onboarding flow, while <c>Users.VehicleType</c> is what an active driver actually carries.
/// On dev only 3 of 46 driver wallets had an application row, so anything reading the
/// application alone — the cashbond status query did — saw no vehicle type for nearly every
/// driver and silently reported nothing due.
///
/// Kept as a contract rather than a direct Identity reference for the same reason as
/// <see cref="GetUserContactQuery"/>: the Drivers module does not reference Identity.
/// </remarks>
public record GetUserVehicleTypeQuery(Guid UserId) : IRequest<Result<string?>>;
