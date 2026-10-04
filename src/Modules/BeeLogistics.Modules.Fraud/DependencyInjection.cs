using BeeLogistics.Modules.Fraud.Application;
using BeeLogistics.Modules.Fraud.Application.Consumers;
using BeeLogistics.Modules.Fraud.Application.Interfaces;
using BeeLogistics.Modules.Fraud.Infrastructure;
using BeeLogistics.Modules.Fraud.Infrastructure.Repositories;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeeLogistics.Modules.Fraud;

public static class DependencyInjection
{
    public static IMvcBuilder AddFraudModule(this IMvcBuilder mvcBuilder, string connectionString)
    {
        var services = mvcBuilder.Services;
        services.AddDbContext<FraudDbContext>(options =>
            options.UseNpgsql(connectionString, x =>
            {
                x.MigrationsHistoryTable("__FraudMigrationsHistory", "public");
                x.MigrationsAssembly(typeof(FraudDbContext).Assembly.GetName().Name);
            }));

        services.AddScoped<IFraudEventRepository, FraudEventRepository>();
        services.AddScoped<IFraudSignalRepository, FraudSignalRepository>();

        mvcBuilder.AddApplicationPart(typeof(DependencyInjection).Assembly);

        return mvcBuilder;
    }

    public static void AddFraudConsumers(this IRegistrationConfigurator cfg)
    {
        cfg.AddConsumer<BookingCompletedFraudConsumer>();
        cfg.AddConsumer<OrderCreatedFraudConsumer>();
        cfg.AddConsumer<DriverLocationUpdatedFraudConsumer>();
        cfg.AddConsumer<UserRegisteredFraudConsumer>();
    }
}
