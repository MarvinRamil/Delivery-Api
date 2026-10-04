using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Asks for the vehicle types a driver may be registered under. Handled by the Bookings
/// module, which owns the vehicle pricing table — the single source of truth for which
/// vehicle types exist.
///
/// Lives in Shared.Contracts so the Drivers module can validate a driver application
/// without referencing Bookings' domain or repositories: the query and its result are
/// plain strings, and MediatR resolves the handler at runtime (every module assembly is
/// registered in Program.cs).
/// </summary>
/// <remarks>
/// Deliberately returns names only, never pricing rows — consumers decide who may register
/// for a type, not what a booking in that class costs. Keeping fare data out of this
/// contract stops the pricing model leaking across the module boundary.
///
/// The casing returned is authoritative: callers should store that spelling rather than
/// whatever a client submitted, so later fare lookups match the pricing row.
/// </remarks>
public record GetActiveVehicleTypesQuery : IRequest<Result<IReadOnlyList<string>>>;
