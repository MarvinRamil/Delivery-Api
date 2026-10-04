using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Query to get dispatches by driver ID. Used for cross-module communication.
/// Implemented by Operations module.
/// </summary>
public record GetDispatchesByDriverQuery(Guid DriverId) : IRequest<Result<object>>;

/// <summary>
/// Query to get dispatches by their IDs. Used for cross-module communication.
/// Implemented by Operations module.
/// </summary>
public record GetDispatchesByIdsQuery(IEnumerable<Guid> DispatchIds) : IRequest<Result<object>>;

