using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Messaging.Application.Services;

/// <summary>
/// Turns a bee Identity user into a Matrix account, once, and remembers the mapping.
/// </summary>
public interface IMatrixUserProvisioner
{
    /// <summary>
    /// Returns the MXID for a bee user, creating the account on first use. Idempotent and safe to
    /// call from a consumer that may be retried.
    /// </summary>
    Task<string> EnsureAsync(string beeUserId, string? displayName, CancellationToken ct = default);
}

public sealed class MatrixUserProvisioner : IMatrixUserProvisioner
{
    private readonly IMatrixAppServiceClient _client;
    private readonly IMatrixIdentityRepository _identities;
    private readonly MatrixOptions _options;
    private readonly ILogger<MatrixUserProvisioner> _logger;

    public MatrixUserProvisioner(
        IMatrixAppServiceClient client,
        IMatrixIdentityRepository identities,
        IOptions<MatrixOptions> options,
        ILogger<MatrixUserProvisioner> logger)
    {
        _client = client;
        _identities = identities;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> EnsureAsync(string beeUserId, string? displayName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(beeUserId))
            throw new ArgumentException("A bee user id is required to provision a Matrix account.", nameof(beeUserId));

        var known = await _identities.GetByBeeUserIdAsync(beeUserId, ct);
        if (known is not null) return known.MatrixUserId;

        // Derive only when creating. Afterwards the stored value is authoritative, so a later
        // change to EnvironmentPrefix or ServerName cannot orphan existing rooms by silently
        // pointing at a different account.
        var localpart = $"{_options.UserLocalpartPrefix}{beeUserId.ToLowerInvariant()}";
        var mxid = await _client.EnsureUserAsync(localpart, displayName, ct);

        var stored = await _identities.AddOrGetAsync(new MatrixIdentity(beeUserId, mxid), ct);

        if (stored.MatrixUserId != mxid)
        {
            // Someone else provisioned this user first and got a different MXID. That should be
            // impossible — the localpart is a pure function of the bee user id — so it means the
            // prefix or server name changed under us. Use the stored one and say so loudly.
            _logger.LogWarning(
                "Matrix identity for {BeeUserId} already existed as {Stored} but this environment derived {Derived}. " +
                "Using the stored value. Check whether Matrix:EnvironmentPrefix or Matrix:ServerName changed.",
                beeUserId, stored.MatrixUserId, mxid);
        }

        return stored.MatrixUserId;
    }
}
