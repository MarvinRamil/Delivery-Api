using BeeLogistics.Modules.Revenue.Application.Consumers;
using BeeLogistics.Modules.Revenue.Application.Interfaces;
using BeeLogistics.Modules.Revenue.Infrastructure;
using BeeLogistics.Modules.Revenue.Infrastructure.Repositories;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeeLogistics.Modules.Revenue;

public static class DependencyInjection
{
    public static IMvcBuilder AddRevenueModule(this IMvcBuilder mvcBuilder, string connectionString)
    {
        var services = mvcBuilder.Services;

        services.AddDbContext<RevenueDbContext>(options =>
            options.UseNpgsql(connectionString, x => x.MigrationsHistoryTable("__RevenueMigrationsHistory", "revenue")));

        services.AddScoped<IPlatformCommissionRepository, PlatformCommissionRepository>();

        // Register MediatR handlers
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));

        mvcBuilder.AddApplicationPart(typeof(DependencyInjection).Assembly);

        return mvcBuilder;
    }

    public static void AddRevenueConsumers(this IRegistrationConfigurator cfg)
    {
        cfg.AddConsumer<PaymentRefundedRevenueConsumer>();
    }
}
