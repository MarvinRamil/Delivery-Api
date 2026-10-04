using BeeLogistics.Modules.Verification.Application.Interfaces;
using BeeLogistics.Modules.Verification.Application.Services;
using BeeLogistics.Modules.Verification.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BeeLogistics.Modules.Verification;

public static class DependencyInjection
{
    public static IMvcBuilder AddVerificationModule(this IMvcBuilder mvcBuilder, IConfiguration configuration, string connectionString)
    {
        var services = mvcBuilder.Services;
        services.Configure<LivenessOptions>(configuration.GetSection(LivenessOptions.SectionName));
        services.Configure<DiditOptions>(configuration.GetSection(DiditOptions.SectionName));
        services.Configure<FaceMatchOptions>(configuration.GetSection(FaceMatchOptions.SectionName));
        services.Configure<ShiftCheckOptions>(configuration.GetSection(ShiftCheckOptions.SectionName));

        services.AddDbContext<VerificationDbContext>(options =>
            options.UseNpgsql(connectionString, x => x.MigrationsHistoryTable("__VerificationMigrationsHistory", "public")));

        services.AddHttpClient<ILivenessApiClient, LivenessApiClient>();
        services.AddSingleton<ILivenessSessionStore, InMemoryLivenessSessionStore>();
        services.AddScoped<ILivenessSessionService, LivenessSessionService>();
        services.AddHostedService<LivenessHeartbeatService>();

        services.AddHttpClient<IDiditApiClient, DiditApiClient>();
        services.AddScoped<IKycService, KycService>();
        services.AddScoped<ICustomerKycService, CustomerKycService>();
        services.AddScoped<IDriverVerificationLookup, DriverVerificationLookup>();
        services.AddScoped<IDriverIdDocumentProvider, DriverIdDocumentProvider>();

        // Per-shift face check (self-hosted InsightFace match vs the KYC reference selfie)
        services.AddHttpClient<IFaceMatchApiClient, FaceMatchApiClient>();
        services.AddSingleton<IShiftCheckSessionStore, InMemoryShiftCheckSessionStore>();
        services.AddScoped<ShiftCheckService>();
        services.AddScoped<IShiftCheckService>(sp => sp.GetRequiredService<ShiftCheckService>());
        services.AddScoped<IShiftCheckGate>(sp => sp.GetRequiredService<ShiftCheckService>());

        mvcBuilder.AddApplicationPart(typeof(DependencyInjection).Assembly);

        return mvcBuilder;
    }
}
