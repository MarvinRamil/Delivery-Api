using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Application.Services;
using BeeLogistics.Modules.Notification.Infrastructure;
using BeeLogistics.Modules.Notification.Infrastructure.Configuration;
using BeeLogistics.Modules.Notification.Infrastructure.Repositories;
using BeeLogistics.Modules.Notification.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace BeeLogistics.Modules.Notification;

public static class DependencyInjection
{
    public static IMvcBuilder AddNotificationModule(
        this IMvcBuilder mvcBuilder,
        string connectionString,
        IConfiguration configuration)
    {
        var services = mvcBuilder.Services;

        services.Configure<SmsOptions>(configuration.GetSection(SmsOptions.SectionName));

        // Database
        services.AddDbContext<NotificationDbContext>(options =>
            options.UseNpgsql(connectionString, x => x.MigrationsHistoryTable("__NotificationMigrationsHistory", "public")));

        // Repositories
        services.AddScoped<IDeviceTokenRepository, DeviceTokenRepository>();
        services.AddScoped<IEmailRecordRepository, EmailRecordRepository>();
        services.AddScoped<ISmsRecordRepository, SmsRecordRepository>();
        services.AddScoped<IPushRecordRepository, PushRecordRepository>();

        // Services
        services.AddScoped<IEmailTemplateService, EmailTemplateService>();
        services.AddScoped<SmtpEmailService>();
        services.AddScoped<IEmailService, EmailQueueService>();

        // SMS providers (typed HttpClient + per-provider resilience)
        services.AddHttpClient<IlocosSmsProvider>((sp, client) =>
        {
            var cfg = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SmsOptions>>().Value.Providers.IlocosSms;
            client.BaseAddress = new Uri(cfg.ApiBaseUrl.TrimEnd('/') + "/");
        })
        .AddStandardResilienceHandler(SmsResilienceExtensions.ConfigureSmsHttpResilience);

        services.AddHttpClient<PhilSmsProvider>()
        .AddStandardResilienceHandler(SmsResilienceExtensions.ConfigureSmsHttpResilience);

        services.AddScoped<ISmsService, FailoverSmsService>();
        services.AddScoped<ISmsNotificationService, SmsNotificationService>();

        services.AddScoped<IBookingEmailService, BookingEmailService>();

        services.AddHttpClient();

        // Push providers + flag-based routing (Part A). Both concrete providers are
        // registered; the active IPushNotificationService is the router by default,
        // which selects Firebase vs Expo per token from the TokenType flag.
        services.AddScoped<ExpoPushNotificationService>();
        services.AddScoped<FirebaseNotificationService>();
        services.AddScoped<RoutingPushNotificationService>();
        services.AddScoped<IFirebaseNotificationService>(sp => sp.GetRequiredService<FirebaseNotificationService>());
        services.AddScoped<IPushProviderPolicy, PushProviderPolicy>();

        // "Push:Provider" is an optional global override: "Firebase" | "Expo" forces a
        // single provider; unset (default) uses the per-token flag router.
        services.AddScoped<IPushNotificationService>(sp =>
        {
            var provider = configuration["Push:Provider"]?.Trim().ToLowerInvariant();
            return provider switch
            {
                "firebase" => sp.GetRequiredService<FirebaseNotificationService>(),
                "expo" => sp.GetRequiredService<ExpoPushNotificationService>(),
                _ => sp.GetRequiredService<RoutingPushNotificationService>()
            };
        });
        services.AddScoped<CombinedNotificationService>();

        // Single entry point for push: writes a Queued record and publishes to the SendPush queue.
        services.AddScoped<BeeLogistics.Shared.Contracts.IPushDispatcher, PushDispatchService>();

        services.AddScoped<EmailSendingJob>();

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));

        mvcBuilder.AddApplicationPart(typeof(DependencyInjection).Assembly);

        return mvcBuilder;
    }
}
