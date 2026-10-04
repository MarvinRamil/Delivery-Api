using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Messaging.Domain;

/// <summary>
/// The mapping between a bee Identity user and the Matrix account this environment provisioned
/// for them. One Matrix account per bee identity, so someone who is both a customer and a driver
/// has a single account rather than two.
/// </summary>
/// <remarks>
/// This table is the reason the appservice never has to guess. Deriving the MXID from the user id
/// on the fly would work right up until <c>Matrix:EnvironmentPrefix</c> or <c>ServerName</c>
/// changed, at which point every historical room would reference accounts we could no longer
/// resolve. Recording what was actually created keeps history readable.
/// </remarks>
public class MatrixIdentity : Entity
{
    private MatrixIdentity() { }

    public MatrixIdentity(string beeUserId, string matrixUserId)
    {
        Id = Guid.NewGuid();
        BeeUserId = beeUserId;
        MatrixUserId = matrixUserId;
    }

    /// <summary>The ASP.NET Identity user id — the JWT <c>sub</c> / <c>NameIdentifier</c> claim.</summary>
    public string BeeUserId { get; private set; } = string.Empty;

    /// <summary>Full MXID, e.g. <c>@bee_u_dev_{guid}:matrix.bee-app.tech</c>.</summary>
    public string MatrixUserId { get; private set; } = string.Empty;
}
