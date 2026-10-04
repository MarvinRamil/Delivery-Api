using BeeLogistics.Modules.Giveaways.Application;
using BeeLogistics.Modules.Giveaways.Application.Interfaces;
using BeeLogistics.Modules.Giveaways.Infrastructure;
using BeeLogistics.Modules.Giveaways.Infrastructure.Repositories;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeeLogistics.Modules.Giveaways;

public static class DependencyInjection
{
    public static IMvcBuilder AddGiveawaysModule(this IMvcBuilder mvcBuilder, string connectionString)
    {
        var services = mvcBuilder.Services;

        services.AddDbContext<GiveawaysDbContext>(options =>
            options.UseNpgsql(connectionString, x => x.MigrationsHistoryTable("__GiveawaysMigrationsHistory", "public")));

        services.AddScoped<IGiveawayRepository, GiveawayRepository>();
        services.AddScoped<IRaffleDrawService, RaffleDrawService>();
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);

        mvcBuilder.AddApplicationPart(typeof(DependencyInjection).Assembly);
        return mvcBuilder;
    }
}
