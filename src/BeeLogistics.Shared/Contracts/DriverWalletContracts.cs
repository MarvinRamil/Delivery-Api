using MediatR;

namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Of these drivers, which may be offered a cash job of this fare — that is, which ones we could
/// actually collect the platform commission from afterwards (issue #102).
///
/// Handled by the Drivers module, which owns wallets and the commission rate.
///
/// Lives in Shared.Contracts because the dependency only runs one way: Drivers references Bookings,
/// so Bookings cannot reference Drivers back. The query and its result are plain ids, and MediatR
/// resolves the handler at runtime (every module assembly is registered in Program.cs).
/// </summary>
/// <remarks>
/// Takes the <b>fare</b>, not the commission. The rate lives with the wallet options, so a caller
/// never has to know it and can never disagree with the handler that later charges it.
///
/// <para>
/// Deliberately returns ids only, never balances: dispatch decides who to offer work to, not what
/// anyone is worth. Keeping wallet figures out of this contract stops driver finances leaking into
/// the matching path, where they would end up logged with booking diagnostics.
/// </para>
/// <para>
/// The whole candidate set goes in one call. This runs in front of every booking, so a query per
/// candidate would put the wallet check in the way of dispatch itself.
/// </para>
/// </remarks>
public record GetDriversEligibleForCashJobQuery(IReadOnlyCollection<Guid> DriverIds, decimal Fare)
    : IRequest<IReadOnlyList<Guid>>;
