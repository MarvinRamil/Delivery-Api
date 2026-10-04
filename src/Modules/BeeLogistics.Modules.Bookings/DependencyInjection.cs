using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Infrastructure;
using BeeLogistics.Modules.Bookings.Infrastructure.Repositories;
using BeeLogistics.Modules.Bookings.Infrastructure.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeeLogistics.Modules.Bookings;

public static class DependencyInjection
{
    public static IMvcBuilder AddBookingsModule(this IMvcBuilder mvcBuilder, string connectionString)
    {
        var services = mvcBuilder.Services;

        services.AddDbContext<BookingsDbContext>(options =>
            options.UseNpgsql(connectionString, x => x.MigrationsHistoryTable("__SalesMigrationsHistory", "public")));

        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IBookingRepository, BookingRepository>();
        services.AddScoped<IDriverBookingOfferRepository, DriverBookingOfferRepository>();
        services.AddScoped<IFavouriteDriverRepository, FavouriteDriverRepository>();
        services.AddScoped<IVehiclePricingRepository, VehiclePricingRepository>();
        
        // Services
        services.AddScoped<BeeLogistics.Shared.Contracts.ILiveTrackingAuthorizer, LiveTrackingAuthorizer>();
        services.AddScoped<BeeLogistics.Shared.Contracts.IBookingOwnershipVerifier, BookingOwnershipVerifier>();
        services.AddScoped<IBookingAccessPolicy, BookingAccessPolicy>();
        services.AddScoped<IDriverDisplayInfoProvider, DriverDisplayInfoProvider>();
        services.AddScoped<IDriverOfferNotifier, DriverOfferNotifier>();
        services.AddSingleton<IBroadcastScheduler, HangfireBroadcastScheduler>();
        services.AddScoped<IDriverAvailabilityService, DriverAvailabilityService>();
        services.AddScoped<IPricingService, PricingService>();
        services.AddScoped<IPricingConfigurationService, PricingConfigurationService>();
        // Mapbox Directions for real road distance, with the Haversine implementation retained
        // inside it as the fallback. Registered via AddHttpClient so the handler is pooled and
        // picks up the existing OpenTelemetry HTTP instrumentation.
        services.AddHttpClient<IRouteDistanceCalculationService, MapboxRouteDistanceService>();
        services.AddScoped<IHighDemandSurchargeService, HighDemandSurchargeService>();
        
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);

        // Note: BookingBroadcastQueueService is now registered in Program.cs for Hangfire

        mvcBuilder.AddApplicationPart(typeof(DependencyInjection).Assembly);

        return mvcBuilder;
    }
}
