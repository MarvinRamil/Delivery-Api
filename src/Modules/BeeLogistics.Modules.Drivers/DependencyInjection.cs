using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Infrastructure;
using BeeLogistics.Modules.Drivers.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeeLogistics.Modules.Drivers;

public static class DependencyInjection
{
    public static IMvcBuilder AddDriversModule(this IMvcBuilder mvcBuilder, string connectionString)
    {
        var services = mvcBuilder.Services;

        services.AddDbContext<DriversDbContext>((sp, options) =>
            options.UseNpgsql(connectionString, x => x.MigrationsHistoryTable("__DriversMigrationsHistory", "public"))
                   .AddInterceptors(sp.GetRequiredService<BeeLogistics.Shared.Infrastructure.Security.BlindIndexSaveChangesInterceptor>()));

        services.AddScoped<IDriverWalletRepository, DriverWalletRepository>();
        services.AddScoped<IDriverApplicationRepository, DriverApplicationRepository>();
        services.AddScoped<ISavedWithdrawalMethodRepository, SavedWithdrawalMethodRepository>();
        services.AddScoped<IDriverCashBondConfigRepository, DriverCashBondConfigRepository>();
        services.AddScoped<IDriverPackageInsuranceFeeConfigRepository, DriverPackageInsuranceFeeConfigRepository>();
        // NOTE: Bank account numbers are encrypted at rest by the EF value converter on
        // SavedWithdrawalMethod.AccountNumber (see DriversDbContext). No separate encryption
        // service is needed.

        // Register MediatR handlers
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));

        mvcBuilder.AddApplicationPart(typeof(DependencyInjection).Assembly);

        return mvcBuilder;
    }
}

