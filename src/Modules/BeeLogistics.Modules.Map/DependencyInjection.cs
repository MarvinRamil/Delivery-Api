using BeeLogistics.Modules.Map.Application;
using BeeLogistics.Modules.Map.Application.Consumers;
using BeeLogistics.Modules.Map.Application.Handlers;
using BeeLogistics.Modules.Map.Application.Interfaces;
using BeeLogistics.Modules.Map.Application.Services;
using BeeLogistics.Modules.Map.Infrastructure;
using BeeLogistics.Modules.Map.Infrastructure.Repositories;
using BeeLogistics.Modules.Map.Infrastructure.Services;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BeeLogistics.Modules.Map;

public static class DependencyInjection
{
    public static IServiceCollection AddMapModule(this IServiceCollection services, IConfiguration configuration)
    {
        // Database
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        services.AddDbContext<MapDbContext>(options =>
            options.UseNpgsql(connectionString, x => 
            {
                x.MigrationsHistoryTable("__MapMigrationsHistory", "public");
                // Enable PostGIS support for geospatial queries
                x.UseNetTopologySuite();
            }));

        // Redis (for geospatial caching). This multiplexer is the app-wide singleton — it also
        // backs RedisCacheService prefix invalidation, RedisHealthCheck, and the payment
        // top-up/booking-paid pub/sub broadcasters, so it must dial the same Redis as the
        // distributed cache. Resolve Redis:ConnectionString first, then fall back to
        // ConnectionStrings:Redis (the key Program.cs uses) so a single configured key keeps
        // every Redis consumer on the same host.
        var redisConnectionString = configuration["Redis:ConnectionString"]
            ?? configuration.GetConnectionString("Redis");
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<IConnectionMultiplexer>>();
            var connectionString = redisConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                var environment = sp.GetRequiredService<IHostEnvironment>();
                if (!environment.IsDevelopment())
                    throw new InvalidOperationException(
                        "Redis connection string is required. Set Redis__ConnectionString or " +
                        "ConnectionStrings__Redis (a localhost fallback is only allowed in Development).");

                connectionString = "localhost:6379";
            }

            logger.LogInformation("Configuring Redis multiplexer: {RedisConnection}", connectionString);
            var config = ConfigurationOptions.Parse(connectionString);
            config.AbortOnConnectFail = false; // Prevent startup failure if Redis is down
            config.ConnectRetry = 5;
            config.ConnectTimeout = 10000;
            return ConnectionMultiplexer.Connect(config);
        });

        // Repositories
        services.AddScoped<ILocationRepository, LocationRepository>();
        services.AddScoped<IGeofenceRepository, GeofenceRepository>();
        services.AddScoped<IDriverGeofenceStateRepository, DriverGeofenceStateRepository>();

        // Services
        services.AddSingleton<IRedisLocationCache, RedisLocationCache>();
        services.AddSingleton<IDriverGeoIndex, H3DriverGeoIndex>();
        services.AddSingleton<ILocationHistoryBatchWriter, LocationHistoryBatchWriter>();
        services.AddScoped<IDistanceCalculationService, PostGISDistanceService>();
        // Singleton on purpose. The sanitizer stays scoped (it depends on the scoped
        // IDistanceCalculationService), but the per-driver rate-limit budget and last-known
        // position must outlive a single message or the checks that read them never fire - which is
        // exactly what was happening while that state lived in fields on the scoped sanitizer.
        services.AddSingleton<IDriverLocationStateTracker, DriverLocationStateTracker>();
        services.AddOptions<LocationRetentionOptions>()
            .Bind(configuration.GetSection(LocationRetentionOptions.SectionName));
        services.AddSingleton<IValidateOptions<LocationRetentionOptions>, LocationRetentionOptionsValidator>();
        services.AddScoped<ILocationHistoryRetentionService, LocationHistoryRetentionService>();

        services.AddScoped<ILocationSanitizer, LocationSanitizer>();
        // The one ingest pipeline for every transport (MQTT and HTTP). Scoped because the
        // sanitizer it wraps is scoped.
        services.AddScoped<ILocationIngestService, LocationIngestService>();
        services.AddScoped<IMqttTokenService, MqttTokenService>();
        services.AddScoped<IGeofenceService, GeofenceService>();
        services.AddSingleton<MqttConnectionStatus>();
        services.AddSingleton<IMqttConnectionStatus>(sp => sp.GetRequiredService<MqttConnectionStatus>());

        // Background Services
        services.AddHostedService<MqttLocationSubscriberService>();

        // MediatR
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));

        return services;
    }

    /// <summary>
    /// Configure MassTransit consumers for location events
    /// Called from main Program.cs where MassTransit is configured
    /// </summary>
    public static void AddLocationConsumers(this IRegistrationConfigurator cfg)
    {
        cfg.AddConsumer<DriverLocationUpdatedConsumer>();
        cfg.AddConsumer<GeofenceCheckConsumer>();
    }
}
