using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Fallback;

namespace BeeLogistics.Modules.Notification.Infrastructure.Services;

/// <summary>
/// Orchestrates SMS sending: Ilocos relay primary with Polly fallback to PhilSMS.
/// </summary>
public class FailoverSmsService : ISmsService
{
    private static readonly ResiliencePropertyKey<string> PhoneKey = new("sms.phone");
    private static readonly ResiliencePropertyKey<string> MessageKey = new("sms.message");

    private readonly IlocosSmsProvider _ilocosProvider;
    private readonly PhilSmsProvider _philProvider;
    private readonly SmsOptions _options;
    private readonly ILogger<FailoverSmsService> _logger;
    private readonly ResiliencePipeline<SmsSendResult> _pipeline;

    public FailoverSmsService(
        IlocosSmsProvider ilocosProvider,
        PhilSmsProvider philProvider,
        IOptions<SmsOptions> options,
        ILogger<FailoverSmsService> logger)
    {
        _ilocosProvider = ilocosProvider;
        _philProvider = philProvider;
        _options = options.Value;
        _logger = logger;

        _pipeline = new ResiliencePipelineBuilder<SmsSendResult>()
            .AddFallback(new FallbackStrategyOptions<SmsSendResult>
            {
                ShouldHandle = args =>
                {
                    if (args.Outcome.Exception is not null)
                    {
                        return PredicateResult.True();
                    }

                    return args.Outcome.Result is { Success: false, ShouldFailover: true }
                        ? PredicateResult.True()
                        : PredicateResult.False();
                },
                OnFallback = args =>
                {
                    var reason = args.Outcome.Exception?.Message
                                 ?? args.Outcome.Result?.ErrorCode
                                 ?? "unknown";
                    _logger.LogWarning(
                        "IlocosSms failed ({Reason}), failing over to PhilSMS",
                        reason);
                    return default;
                },
                FallbackAction = async args =>
                {
                    var phone = args.Context.Properties.GetValue(PhoneKey, string.Empty);
                    var message = args.Context.Properties.GetValue(MessageKey, string.Empty);
                    var result = await _philProvider.SendAsync(phone, message, args.Context.CancellationToken);
                    return Outcome.FromResult(result);
                }
            })
            .Build();
    }

    public async Task<bool> SendAsync(string toPhoneNumber, string message, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_options.ForceProvider))
        {
            return await SendViaForcedProviderAsync(_options.ForceProvider, toPhoneNumber, message, ct);
        }

        var context = ResilienceContextPool.Shared.Get(ct);
        context.Properties.Set(PhoneKey, toPhoneNumber);
        context.Properties.Set(MessageKey, message);

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await _pipeline.ExecuteAsync(
                async ctx => await _ilocosProvider.SendAsync(
                    toPhoneNumber,
                    message,
                    ctx.CancellationToken),
                context);

            sw.Stop();

            if (result.Success)
            {
                _logger.LogInformation(
                    "SMS sent via {Provider} in {DurationMs}ms",
                    result.Provider,
                    sw.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogWarning(
                    "SMS send failed after failover. Last provider={Provider}, Error={Error}, DurationMs={DurationMs}",
                    result.Provider,
                    result.ErrorCode,
                    sw.ElapsedMilliseconds);
            }

            return result.Success;
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    private async Task<bool> SendViaForcedProviderAsync(
        string forceProvider,
        string toPhoneNumber,
        string message,
        CancellationToken ct)
    {
        _logger.LogInformation("Sms:ForceProvider={ForceProvider} — bypassing failover pipeline", forceProvider);

        var result = string.Equals(forceProvider, PhilSmsProvider.ProviderName, StringComparison.OrdinalIgnoreCase)
            ? await _philProvider.SendAsync(toPhoneNumber, message, ct)
            : await _ilocosProvider.SendAsync(toPhoneNumber, message, ct);

        return result.Success;
    }
}
