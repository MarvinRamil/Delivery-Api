using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace BeeLogistics.Modules.Messaging.Infrastructure;

/// <summary>
/// Shared wiring for the HttpClient that talks to Synapse.
/// </summary>
/// <remarks>
/// The client's <c>BaseAddress</c> is normalised to end in a slash, so every relative path passed
/// to it must be written <b>without</b> a leading slash (<c>"_matrix/client/versions"</c>, not
/// <c>"/_matrix/client/versions"</c>). A leading slash resolves against the host root and silently
/// discards any path prefix in the configured homeserver URL.
/// </remarks>
public static class MatrixHttpClient
{
    public const string Name = "matrix";

    /// <summary>
    /// Guarantees the trailing slash <see cref="Uri"/> relative resolution needs, so a homeserver
    /// URL is safe to configure with or without one.
    /// </summary>
    public static Uri NormalizeBaseAddress(string homeserverUrl)
    {
        var trimmed = homeserverUrl.TrimEnd('/');
        return new Uri(trimmed + "/", UriKind.Absolute);
    }

    /// <summary>
    /// Retries transient failures and breaks the circuit on a sustained outage.
    /// </summary>
    /// <remarks>
    /// Deliberately unlike the Didit and FaceMatch clients, which have only a per-request timeout:
    /// those are called on a user-facing path where a fast failure is the right answer, whereas
    /// every Matrix call here is made from a MassTransit consumer that will be retried anyway.
    /// Absorbing a blip in-process beats bouncing the whole message through the broker.
    ///
    /// Only idempotent-by-construction calls reach this client — room creation is keyed on the
    /// room alias and message sends carry a client-generated transaction id — so a retry cannot
    /// duplicate a room or a message.
    /// </remarks>
    public static void Configure(HttpStandardResilienceOptions options)
    {
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.Delay = TimeSpan.FromSeconds(1);
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        options.Retry.ShouldHandle = args =>
        {
            var statusCode = args.Outcome.Result?.StatusCode;

            // 429 is Synapse rate limiting. The appservice is registered rate_limited: false, so
            // seeing one means the registration file and this config have drifted apart — still
            // worth backing off rather than hammering.
            var isTransientStatus =
                statusCode == HttpStatusCode.TooManyRequests ||
                (statusCode.HasValue && (int)statusCode.Value >= 500);

            var isTransientException = args.Outcome.Exception is HttpRequestException or TaskCanceledException;

            return ValueTask.FromResult(isTransientStatus || isTransientException);
        };

        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(60);
        options.CircuitBreaker.MinimumThroughput = 5;
    }
}
