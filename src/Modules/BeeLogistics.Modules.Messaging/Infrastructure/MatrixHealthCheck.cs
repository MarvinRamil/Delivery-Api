using BeeLogistics.Modules.Messaging.Application;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Messaging.Infrastructure;

/// <summary>
/// Reports whether the Matrix homeserver is reachable.
/// </summary>
/// <remarks>
/// <para>
/// <b>This check never returns Unhealthy, by design.</b> It is tagged "ready" so a Synapse outage
/// is visible on <c>/health/ready</c>, but it reports <see cref="HealthStatus.Degraded"/> instead —
/// which the readiness endpoint still answers 200 for. Booking chat is not on the critical path for
/// creating or completing a delivery, and taking the whole API out of rotation because the chat
/// server is down would turn a degraded feature into an outage.
/// </para>
/// <para>
/// <c>GET /_matrix/client/versions</c> is the probe: unauthenticated, no database work on Synapse's
/// side, and it proves the process is actually serving the client API rather than just holding a
/// socket open.
/// </para>
/// </remarks>
public sealed class MatrixHealthCheck : IHealthCheck
{
    /// <summary>
    /// Hard ceiling on the probe, independent of the client's retry budget.
    /// </summary>
    /// <remarks>
    /// The shared Matrix client retries three times inside a 45-second budget, which is right for
    /// a consumer doing real work and badly wrong for a readiness probe — an unreachable Synapse
    /// would stall <c>/health/ready</c> long past any sensible probe timeout and make a chat
    /// outage look like an API hang. One attempt, answered quickly, is all this needs.
    /// </remarks>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MatrixOptions _options;

    public MatrixHealthCheck(IHttpClientFactory httpClientFactory, IOptions<MatrixOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return HealthCheckResult.Healthy("Matrix disabled");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            var client = _httpClientFactory.CreateClient(MatrixHttpClient.Name);

            // Relative, no leading slash — see the remarks on MatrixHttpClient.
            using var response = await client.GetAsync("_matrix/client/versions", timeout.Token);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy($"Synapse reachable at {_options.HomeserverUrl}")
                : HealthCheckResult.Degraded(
                    $"Synapse at {_options.HomeserverUrl} returned {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host cancelled the probe — that says nothing about Synapse. Checked before the
            // timeout case below, because our linked token is cancelled in both situations.
            throw;
        }
        catch (OperationCanceledException)
        {
            return HealthCheckResult.Degraded(
                $"Synapse at {_options.HomeserverUrl} did not respond within {ProbeTimeout.TotalSeconds:F0}s");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded($"Synapse unreachable at {_options.HomeserverUrl}", ex);
        }
    }
}
