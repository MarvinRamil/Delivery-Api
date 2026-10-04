using BeeLogistics.Modules.Referrals.Application.Interfaces;
using BeeLogistics.Modules.Referrals.Infrastructure;
using BeeLogistics.Modules.Referrals.Infrastructure.Repositories;
using BeeLogistics.Modules.Referrals.Infrastructure.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BeeLogistics.Modules.Referrals;

public static class DependencyInjection
{
    public static IMvcBuilder AddReferralsModule(this IMvcBuilder mvcBuilder, string connectionString)
    {
        var services = mvcBuilder.Services;

        services.AddDbContext<ReferralsDbContext>(options =>
            options.UseNpgsql(connectionString, x => x.MigrationsHistoryTable("__ReferralsMigrationsHistory", "public")));

        services.AddScoped<IReferralRepository, ReferralRepository>();
        services.AddScoped<IQrCodeService, QrCodeService>();
        
        // Register MediatR handlers
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        mvcBuilder.AddApplicationPart(typeof(DependencyInjection).Assembly);

        return mvcBuilder;
    }
}

