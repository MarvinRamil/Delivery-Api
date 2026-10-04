using BeeLogistics.Modules.Identity.Application;
using BeeLogistics.Modules.Identity.Application.Interfaces;
using BeeLogistics.Modules.Identity.Application.Services;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Identity.Infrastructure;
using BeeLogistics.Modules.Identity.Infrastructure.Repositories;
using BeeLogistics.Modules.Verification.Application.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeeLogistics.Modules.Identity;

public static class DependencyInjection
{
    public static IMvcBuilder AddIdentityModule(this IMvcBuilder mvcBuilder, string connectionString)
    {
        var services = mvcBuilder.Services;

        services.AddDbContext<IdentityAppDbContext>((sp, options) =>
            options.UseNpgsql(connectionString, x => x.MigrationsHistoryTable("__IdentityMigrationsHistory", "public"))
                   .AddInterceptors(sp.GetRequiredService<BeeLogistics.Shared.Infrastructure.Security.BlindIndexSaveChangesInterceptor>()));

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                // OWASP Password Policy (A07:2021 - Identification and Authentication Failures)
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Password.RequiredUniqueChars = 4;

                // Account Lockout (Brute Force Protection)
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.AllowedForNewUsers = true;

                // User settings
                options.User.RequireUniqueEmail = true;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<IdentityAppDbContext>()
            .AddDefaultTokenProviders();

        // Register audit service
        services.AddScoped<IAuditService, AuditService>();

        // Register repositories (Presentation uses interfaces; Infrastructure implements)
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        
        // Register token blacklist service (for immediate token revocation)
        services.AddScoped<ITokenBlacklistService, TokenBlacklistService>();
        
        // Register refresh token service (for token refresh flow)
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();

        // Single place access tokens are minted (see AuthTokenIssuer for the claim matrix)
        services.AddScoped<IAuthTokenIssuer, AuthTokenIssuer>();

        // Driver vehicle assignment + profile picture URL resolution, lifted out of AuthController
        services.AddScoped<IDriverVehicleService, DriverVehicleService>();
        services.AddScoped<IProfilePictureUrlResolver, ProfilePictureUrlResolver>();

        // Stateless "OTP was verified" proof used across the registration endpoints
        services.AddScoped<IRegistrationVerificationTokenService, RegistrationVerificationTokenService>();

        // Register OTP service
        services.AddScoped<OtpService>();

        // OTP channel scaffold (SMS) — email OTP is owned by Clerk. SMS is gated behind
        // Features:SmsOtp and currently a stub; see CLERK_MIGRATION_PLAN.md §2a.
        services.AddScoped<Application.Interfaces.IOtpChannel, SmsOtpChannel>();

        // Liveness: when Verification module completes a check, mark user as verified
        services.AddScoped<IOnLivenessVerified, LivenessVerifiedHandler>();

        // Shift check: when Verification module passes a per-shift face check, stamp LastFaceCheckAt
        services.AddScoped<IOnShiftCheckPassed, ShiftCheckPassedHandler>();

        // Local-role claims transformation: for Clerk-issued tokens (which carry no role),
        // resolve the authoritative role from the DB and inject role claims. No-op for legacy tokens.
        services.AddMemoryCache();
        services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, LocalRoleClaimsTransformation>();

        // Shared Clerk → local user provisioning (webhook + just-in-time on /api/auth/me).
        // Typed HttpClient is used to call the Clerk Backend API for JIT.
        services.AddHttpClient<ClerkUserProvisioningService>();

        // Dev/Swagger-only: mint a Clerk session JWT server-side for API testing (LoginV2Controller).
        services.AddHttpClient<ClerkDevTokenService>();

        mvcBuilder.AddApplicationPart(typeof(DependencyInjection).Assembly);

        return mvcBuilder;
    }
}
