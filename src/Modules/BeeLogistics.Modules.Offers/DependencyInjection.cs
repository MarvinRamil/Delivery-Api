using BeeLogistics.Modules.Offers.Infrastructure;
using BeeLogistics.Modules.Offers.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeeLogistics.Modules.Offers;

public static class DependencyInjection
{
    public static IMvcBuilder AddOffersModule(this IMvcBuilder mvcBuilder, string connectionString)
    {
        var services = mvcBuilder.Services;

        services.AddDbContext<OffersDbContext>(options =>
            options.UseNpgsql(connectionString, x => x.MigrationsHistoryTable("__OffersMigrationsHistory", "public")));

        services.AddScoped<IOfferRepository, OfferRepository>();

        mvcBuilder.AddApplicationPart(typeof(DependencyInjection).Assembly);
        return mvcBuilder;
    }
}
