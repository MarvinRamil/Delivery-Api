using BeeLogistics.Modules.Rating.Application.Interfaces;
using BeeLogistics.Modules.Rating.Infrastructure;
using BeeLogistics.Modules.Rating.Infrastructure.Repositories;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeeLogistics.Modules.Rating;

public static class DependencyInjection
{
    public static IMvcBuilder AddRatingModule(this IMvcBuilder mvcBuilder, string connectionString)
    {
        var services = mvcBuilder.Services;

        services.AddDbContext<RatingDbContext>(options =>
            options.UseNpgsql(connectionString, x => x.MigrationsHistoryTable("__RatingMigrationsHistory", "public")));

        services.AddScoped<IRatingRepository, RatingRepository>();
        services.AddScoped<IDriverRatingRepository, DriverRatingRepository>();

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);

        mvcBuilder.AddApplicationPart(typeof(DependencyInjection).Assembly);

        return mvcBuilder;
    }
}
