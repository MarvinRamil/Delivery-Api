using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Modules.Notification.Infrastructure.Configuration;

internal static class SmsConfigurationHelper
{
    public static PhilSmsProviderOptions ResolvePhilSms(IConfiguration configuration, SmsOptions smsOptions)
    {
        var phil = smsOptions.Providers.PhilSMS;

        if (!string.IsNullOrWhiteSpace(phil.ApiToken))
        {
            return phil;
        }

        var legacy = configuration.GetSection("PhilSMS");
        return new PhilSmsProviderOptions
        {
            ApiBaseUrl = legacy["ApiBaseUrl"]?.TrimEnd('/')
                         ?? phil.ApiBaseUrl.TrimEnd('/'),
            ApiToken = legacy["ApiToken"] ?? string.Empty,
            SenderId = legacy["SenderId"] ?? phil.SenderId
        };
    }
}
