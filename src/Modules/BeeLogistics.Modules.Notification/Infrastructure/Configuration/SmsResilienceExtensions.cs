using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace BeeLogistics.Modules.Notification.Infrastructure.Configuration;

internal static class SmsResilienceExtensions
{
    public static void ConfigureSmsHttpResilience(HttpStandardResilienceOptions options)
    {
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.Delay = TimeSpan.FromSeconds(1);
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        options.Retry.ShouldHandle = args =>
        {
            var statusCode = args.Outcome.Result?.StatusCode;
            var isTransientStatus = statusCode is HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                || (statusCode.HasValue && (int)statusCode.Value >= 500);
            var isTransientException = args.Outcome.Exception is HttpRequestException or TaskCanceledException;
            return ValueTask.FromResult(isTransientStatus || isTransientException);
        };

        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(60);
        options.CircuitBreaker.MinimumThroughput = 5;
    }
}
