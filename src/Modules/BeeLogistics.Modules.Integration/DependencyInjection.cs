using BeeLogistics.Modules.Integration.Application;
using BeeLogistics.Modules.Integration.Application.Consumers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BeeLogistics.Modules.Integration;

public static class DependencyInjection
{
    public static IServiceCollection AddIntegrationModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BackofficeWebhookOptions>(configuration.GetSection(BackofficeWebhookOptions.SectionName));

        services.AddHttpClient(BackofficeWebhookConsumer.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        return services;
    }
}
