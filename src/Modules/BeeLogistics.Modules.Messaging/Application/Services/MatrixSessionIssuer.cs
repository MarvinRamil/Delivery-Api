using BeeLogistics.Modules.Messaging.Application.DTOs;
using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Domain;
using BeeLogistics.Modules.Messaging.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Messaging.Application.Services;

public interface IMatrixSessionIssuer
{
    /// <summary>Mints a Matrix session for a bee user, provisioning their account on first use.</summary>
    Task<MatrixSessionDto> IssueAsync(string beeUserId, string? displayName, string platform, CancellationToken ct = default);
}

/// <summary>
/// Hands a client its own Matrix credentials so it can talk to Synapse directly.
/// </summary>
/// <remarks>
/// <para>
/// The <c>as_token</c> never leaves the backend. What the client receives is an ordinary
/// per-user access token, minted through <c>m.login.application_service</c> — so a compromised
/// client can act as that one user and nothing more.
/// </para>
/// <para>
/// <b>The access token is never persisted.</b> Only the device id is, so a re-login re-uses the
/// device rather than accumulating one per app launch. Storing tokens would build a credential
/// store worth stealing for no benefit.
/// </para>
/// </remarks>
public sealed class MatrixSessionIssuer : IMatrixSessionIssuer
{
    private readonly IMatrixAppServiceClient _client;
    private readonly IMatrixUserProvisioner _users;
    private readonly MessagingDbContext _db;
    private readonly MatrixOptions _options;

    public MatrixSessionIssuer(
        IMatrixAppServiceClient client,
        IMatrixUserProvisioner users,
        MessagingDbContext db,
        IOptions<MatrixOptions> options)
    {
        _client = client;
        _users = users;
        _db = db;
        _options = options.Value;
    }

    public async Task<MatrixSessionDto> IssueAsync(
        string beeUserId, string? displayName, string platform, CancellationToken ct = default)
    {
        var mxid = await _users.EnsureAsync(beeUserId, displayName, ct);

        // Strip the sigil and domain: /login takes the localpart.
        var localpart = mxid.TrimStart('@').Split(':')[0];

        var known = await _db.MatrixDevices
            .FirstOrDefaultAsync(d => d.BeeUserId == beeUserId && d.Platform == platform, ct);

        var session = await _client.LoginAsUserAsync(
            localpart, known?.DeviceId, $"bee {platform}", ct);

        if (known is null)
        {
            _db.MatrixDevices.Add(new MatrixDevice(beeUserId, platform, session.DeviceId));
        }
        else
        {
            known.MarkIssued();
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two simultaneous logins from the same install raced on the unique (user, platform)
            // index. The token above is already valid, so losing the bookkeeping race must not
            // fail the request — the worst case is one extra device on the next login.
        }

        return new MatrixSessionDto
        {
            HomeserverUrl = _options.ClientFacingHomeserverUrl,
            UserId = session.UserId,
            AccessToken = session.AccessToken,
            DeviceId = session.DeviceId,
        };
    }
}
