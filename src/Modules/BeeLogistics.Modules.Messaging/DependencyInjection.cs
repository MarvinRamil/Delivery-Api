using BeeLogistics.Modules.Messaging.Application;
using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Application.Services;
using BeeLogistics.Modules.Messaging.Infrastructure;
using BeeLogistics.Modules.Messaging.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BeeLogistics.Modules.Messaging;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the Matrix appservice integration. Safe to call unconditionally: with
    /// <c>Matrix:Enabled=false</c> this binds options and an HttpClient nothing ever calls.
    /// </summary>
    public static IServiceCollection AddMessagingModule(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        services.AddDbContext<MessagingDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__MessagingMigrationsHistory", MessagingDbContext.Schema)));

        services.AddScoped<IMatrixIdentityRepository, MatrixIdentityRepository>();
        services.AddScoped<IBookingRoomRepository, BookingRoomRepository>();
        services.AddScoped<IMatrixAppServiceClient, MatrixAppServiceClient>();
        services.AddScoped<IMatrixUserProvisioner, MatrixUserProvisioner>();
        services.AddScoped<IBookingRoomProvisioner, BookingRoomProvisioner>();
        services.AddScoped<IRoomEventArchive, RoomEventArchive>();
        services.AddScoped<IMatrixTransactionProcessor, MatrixTransactionProcessor>();
        services.AddScoped<IMatrixSessionIssuer, MatrixSessionIssuer>();
        services.AddScoped<IBookingTranscriptReader, BookingTranscriptReader>();
        services.AddScoped<IBookingRoomLifecycleService, BookingRoomLifecycleService>();
        services.AddScoped<IBookingRoomBackstopSweep, BookingRoomBackstopSweep>();

        var section = configuration.GetSection(MatrixOptions.SectionName);
        services.Configure<MatrixOptions>(section);

        // Bound a second time, eagerly, because the HttpClient's BaseAddress and Timeout are
        // fixed at registration and cannot read IOptions later.
        var options = new MatrixOptions();
        section.Bind(options);

        services.AddHttpClient(MatrixHttpClient.Name, client =>
        {
            // Same predicate the production guard validates against, so a URL that passes startup
            // is exactly the set of URLs that produce a usable BaseAddress here.
            if (MatrixOptions.IsUsableHomeserverUrl(options.HomeserverUrl))
                client.BaseAddress = MatrixHttpClient.NormalizeBaseAddress(options.HomeserverUrl);

            // The resilience handler owns the overall budget (TotalRequestTimeout); this is the
            // per-attempt ceiling, so it must be the shorter of the two or retries never happen.
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 20);
        })
        .AddStandardResilienceHandler(MatrixHttpClient.Configure);

        return services;
    }
}
