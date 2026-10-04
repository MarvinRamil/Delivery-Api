using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Identity.Infrastructure;
using BeeLogistics.Modules.CRM.Domain;
using BeeLogistics.Modules.CRM.Infrastructure;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Drivers.Infrastructure;
using BeeLogistics.Modules.Map.Infrastructure;
using BeeLogistics.Modules.Fraud.Infrastructure;
using BeeLogistics.Modules.Giveaways.Infrastructure;
using BeeLogistics.Modules.Giveaways.Domain;
using BeeLogistics.Modules.Revenue.Infrastructure;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Bookings.Infrastructure;
using BeeLogistics.Modules.Referrals.Domain;
using BeeLogistics.Modules.Referrals.Infrastructure;
using BeeLogistics.Shared.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Api;

public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var services = scope.ServiceProvider;

        try
        {
            var context = services.GetRequiredService<IdentityAppDbContext>();
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
            var configuration = services.GetRequiredService<IConfiguration>();
            var logger = services.GetRequiredService<ILogger<Program>>();

            // SECURITY: Require environment variable - no default password
            var seedPassword = configuration["Seed:DefaultPassword"];
            if (string.IsNullOrEmpty(seedPassword))
            {
                logger.LogError("Seed:DefaultPassword environment variable is required but not configured. Application startup aborted for security.");
                throw new InvalidOperationException(
                    "Seed:DefaultPassword environment variable is required. " +
                    "Set Seed__DefaultPassword environment variable to a secure password.");
            }

            // Ensure Database is Created/Migrated for all modules
            logger.LogInformation("Applying migrations for all DbContexts...");
            
            try
            {
                await context.Database.MigrateAsync();
                logger.LogInformation("✓ Identity migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply Identity migrations");
                throw;
            }
            
            try
            {
                await services.GetRequiredService<BookingsDbContext>().Database.MigrateAsync();
                logger.LogInformation("✓ Bookings migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply Bookings migrations");
                throw;
            }
            
            try
            {
                await services.GetRequiredService<Modules.Payment.Infrastructure.PaymentDbContext>().Database.MigrateAsync();
                logger.LogInformation("✓ Payment migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply Payment migrations");
                throw;
            }
            
            try
            {
                await services.GetRequiredService<Modules.Chat.Infrastructure.ChatDbContext>().Database.MigrateAsync();
                logger.LogInformation("✓ Chat migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply Chat migrations");
                throw;
            }
            
            try
            {
                await services.GetRequiredService<Modules.Messaging.Infrastructure.MessagingDbContext>().Database.MigrateAsync();
                logger.LogInformation("✓ Messaging migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply Messaging migrations");
                throw;
            }

            try
            {
                await services.GetRequiredService<Modules.CRM.Infrastructure.CrmDbContext>().Database.MigrateAsync();
                logger.LogInformation("✓ CRM migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply CRM migrations");
                throw;
            }
            
            try
            {
                await services.GetRequiredService<DriversDbContext>().Database.MigrateAsync();
                logger.LogInformation("✓ Drivers migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply Drivers migrations");
                throw;
            }
            
            try
            {
                await services.GetRequiredService<Modules.Notification.Infrastructure.NotificationDbContext>().Database.MigrateAsync();
                logger.LogInformation("✓ Notification migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply Notification migrations");
                throw;
            }
            
            try
            {
                await services.GetRequiredService<MapDbContext>().Database.MigrateAsync();
                logger.LogInformation("✓ Map migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply Map migrations");
                throw;
            }

            try
            {
                await services.GetRequiredService<FraudDbContext>().Database.MigrateAsync();
                logger.LogInformation("✓ Fraud migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply Fraud migrations");
                throw;
            }

            try
            {
                await services.GetRequiredService<Modules.Verification.Infrastructure.VerificationDbContext>().Database.MigrateAsync();
                logger.LogInformation("✓ Verification migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply Verification migrations");
                throw;
            }
            
            try
            {
                await services.GetRequiredService<Modules.Rating.Infrastructure.RatingDbContext>().Database.MigrateAsync();
                logger.LogInformation("✓ Rating migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply Rating migrations");
                throw;
            }

            try
            {
                await services.GetRequiredService<GiveawaysDbContext>().Database.MigrateAsync();
                logger.LogInformation("✓ Giveaways migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply Giveaways migrations");
                throw;
            }

            try
            {
                var revenueContext = services.GetRequiredService<RevenueDbContext>();
                await revenueContext.Database.MigrateAsync();

                // Verify schema was actually created (avoids false success when history exists but table does not)
                await revenueContext.Database.OpenConnectionAsync();
                try
                {
                    await using var cmd = revenueContext.Database.GetDbConnection().CreateCommand();
                    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'revenue' AND table_name = 'PlatformCommissions')";
                    var exists = (bool)(await cmd.ExecuteScalarAsync() ?? false);
                    if (!exists)
                        throw new InvalidOperationException(
                            "Revenue migration reported success but PlatformCommissions table was not created. " +
                            "Ensure RevenueDbContext uses the same connection string as the main database. " +
                            "If you removed __RevenueMigrationsHistory, restart the application to re-run migrations.");
                }
                finally
                {
                    await revenueContext.Database.CloseConnectionAsync();
                }

                logger.LogInformation("✓ Revenue migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply Revenue migrations");
                throw;
            }

            try
            {
                await services.GetRequiredService<ReferralsDbContext>().Database.MigrateAsync();
                logger.LogInformation("✓ Referrals migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply Referrals migrations");
                throw;
            }

            try
            {
                // Previously omitted entirely, so the accounting tables were never created by the
                // app. LedgerEntries did not exist at all and SaleRecordedAccountingConsumer failed
                // on every completed booking, dead-lettering the sale after its retries.
                var accountingContext = services.GetRequiredService<Modules.Accounting.Infrastructure.AccountingDbContext>();
                await accountingContext.Database.MigrateAsync();

                // Verify schema was actually created (avoids false success when history exists but table does not)
                await accountingContext.Database.OpenConnectionAsync();
                try
                {
                    await using var cmd = accountingContext.Database.GetDbConnection().CreateCommand();
                    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'accounting' AND table_name = 'LedgerEntries')";
                    var exists = (bool)(await cmd.ExecuteScalarAsync() ?? false);
                    if (!exists)
                        throw new InvalidOperationException(
                            "Accounting migration reported success but LedgerEntries table was not created. " +
                            "Ensure AccountingDbContext uses the same connection string as the main database. " +
                            "If you removed __AccountingMigrationsHistory, restart the application to re-run migrations.");
                }
                finally
                {
                    await accountingContext.Database.CloseConnectionAsync();
                }

                logger.LogInformation("✓ Accounting migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply Accounting migrations");
                throw;
            }

            try
            {
                var offersContext = services.GetRequiredService<Modules.Offers.Infrastructure.OffersDbContext>();
                await offersContext.Database.MigrateAsync();

                await offersContext.Database.OpenConnectionAsync();
                try
                {
                    await using var cmd = offersContext.Database.GetDbConnection().CreateCommand();
                    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'offers')";
                    var exists = (bool)(await cmd.ExecuteScalarAsync() ?? false);
                    if (!exists)
                        throw new InvalidOperationException(
                            "Offers migration reported success but the offers schema was not created. " +
                            "Ensure OffersDbContext uses the same connection string as the main database.");
                }
                finally
                {
                    await offersContext.Database.CloseConnectionAsync();
                }

                logger.LogInformation("✓ Offers migrations applied");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply Offers migrations");
                throw;
            }

            logger.LogInformation("All migrations completed successfully");

            // Backfill: drivers verified under the old liveness-only flow get a
            // Status=Legacy verification row so KYC lookups don't 404 and per-shift
            // checks correctly skip them (no reference selfie/embedding to match).
            try
            {
                var verificationContext = services.GetRequiredService<Modules.Verification.Infrastructure.VerificationDbContext>();
                var legacyUserIds = await context.Users
                    .Where(u => u.LivenessVerifiedAt != null)
                    .Select(u => u.Id)
                    .ToListAsync();
                if (legacyUserIds.Count > 0)
                {
                    var alreadyHave = await verificationContext.DriverVerifications
                        .Where(v => legacyUserIds.Contains(v.UserId))
                        .Select(v => v.UserId)
                        .ToListAsync();
                    var missing = legacyUserIds.Except(alreadyHave).ToList();
                    if (missing.Count > 0)
                    {
                        verificationContext.DriverVerifications.AddRange(missing.Select(id =>
                            new Modules.Verification.Domain.DriverVerification
                            {
                                UserId = id,
                                Status = Modules.Verification.Domain.DriverVerificationStatus.Legacy,
                                CompletedAt = DateTime.UtcNow
                            }));
                        await verificationContext.SaveChangesAsync();
                        logger.LogInformation("Backfilled {Count} legacy driver verification rows", missing.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to backfill legacy driver verifications (non-fatal)");
            }

            // Seed Giveaways + Campaigns
            var giveawaysContext = services.GetRequiredService<GiveawaysDbContext>();
            if (!await giveawaysContext.Giveaways.AnyAsync())
            {
                logger.LogInformation("Seeding giveaways and campaigns...");

                var now = DateTime.UtcNow;
                var giveaways = new[]
                {
                    new Giveaway(
                        title: "Weekly Helmet Giveaway",
                        description: "Complete your missions this week for a chance to win a premium helmet.",
                        startDate: now.AddDays(-1),
                        endDate: now.AddDays(6),
                        imagePath: null,
                        rewardDetails: "Premium full-face helmet"),
                    new Giveaway(
                        title: "Fuel Voucher Raffle",
                        description: "Join this giveaway and get a chance to win fuel vouchers.",
                        startDate: now.AddDays(-2),
                        endDate: now.AddDays(10),
                        imagePath: null,
                        rewardDetails: "PHP 1,000 fuel voucher")
                };

                var campaigns = new[]
                {
                    new Campaign(
                        type: CampaignType.Giveaway,
                        title: "Giveaway Live Now",
                        body: "Join the latest giveaway and boost your winning chances today.",
                        startDate: now.AddDays(-1),
                        endDate: now.AddDays(7),
                        imagePath: null,
                        ctaText: "View Giveaways",
                        ctaRoute: "/(tabs)/giveaways"),
                    new Campaign(
                        type: CampaignType.News,
                        title: "New Driver Rewards Program",
                        body: "Check the updated missions and earn more from your weekly activity.",
                        startDate: now.AddDays(-1),
                        endDate: now.AddDays(14),
                        imagePath: null,
                        ctaText: "Open Missions",
                        ctaRoute: "/(tabs)/missions")
                };

                giveawaysContext.Giveaways.AddRange(giveaways);
                giveawaysContext.Campaigns.AddRange(campaigns);
                await giveawaysContext.SaveChangesAsync();
                logger.LogInformation("Seeded {GiveawaysCount} giveaways and {CampaignCount} campaigns", giveaways.Length, campaigns.Length);
            }
            else
            {
                logger.LogInformation("Giveaways already seeded, skipping giveaway/campaign seed");
            }

            // Seed Referrals
            var referralsContext = services.GetRequiredService<ReferralsDbContext>();
            if (!await referralsContext.ReferralCodes.AnyAsync())
            {
                logger.LogInformation("Seeding referrals...");

                var baseUrl = configuration["Referrals:BaseUrl"] ?? "https://mybeeapp.com";

                var referrerUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
                var referredUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

                var referralCode = new ReferralCode(
                    userId: referrerUserId,
                    code: "DRVSEED01A1",
                    userType: ReferralUserType.Driver,
                    baseUrl: baseUrl);
                referralsContext.ReferralCodes.Add(referralCode);
                await referralsContext.SaveChangesAsync();

                var referral = new Referral(
                    referralCodeId: referralCode.Id,
                    referrerId: referrerUserId,
                    referredUserId: referredUserId,
                    referrerType: ReferralUserType.Driver,
                    referredType: ReferralUserType.Driver);
                referral.MarkAsCompleted();
                referral.AwardPoints(100);
                referralsContext.Referrals.Add(referral);
                await referralsContext.SaveChangesAsync();

                var userPoints = new UserPoints(referrerUserId);
                userPoints.AddAvailablePoints(100);

                var transaction = new PointsTransaction(
                    userId: referrerUserId,
                    type: PointsTransactionType.ReferralEarned,
                    points: 100,
                    balanceAfter: userPoints.AvailablePoints,
                    description: "Seed referral reward points",
                    relatedReferralId: referral.Id);

                referralsContext.UserPoints.Add(userPoints);
                referralsContext.PointsTransactions.Add(transaction);
                await referralsContext.SaveChangesAsync();
                logger.LogInformation("Seeded referral data successfully");
            }
            else
            {
                logger.LogInformation("Referrals already seeded, skipping referral seed");
            }

            // Seed Global Missions
            var driversContext = services.GetRequiredService<DriversDbContext>();
            if (!await driversContext.GlobalMissions.AnyAsync())
            {
                logger.LogInformation("Seeding global missions...");

                var now = DateTime.UtcNow;
                var globalMissions = new[]
                {
                    new GlobalMission(
                        title: "Weekly 20 Trips",
                        description: "Complete 20 trips this week to earn bonus.",
                        reward: 500m,
                        target: 20,
                        type: MissionType.Weekly,
                        expiresAt: now.AddDays(7)),
                    new GlobalMission(
                        title: "Daily 5 Trips",
                        description: "Complete 5 trips today to earn daily bonus.",
                        reward: 120m,
                        target: 5,
                        type: MissionType.Daily,
                        expiresAt: now.AddDays(1))
                };

                driversContext.GlobalMissions.AddRange(globalMissions);
                await driversContext.SaveChangesAsync();

                // Distribute seeded global missions to existing active drivers
                var activeDrivers = await userManager.Users
                    .Where(u => u.Role == UserRoles.Driver && u.IsActive)
                    .Select(u => u.Id)
                    .ToListAsync();

                var issuedCount = 0;
                foreach (var driverIdRaw in activeDrivers)
                {
                    if (!Guid.TryParse(driverIdRaw, out var driverId))
                        continue;

                    foreach (var gm in globalMissions)
                    {
                        var mission = new DriverMission(
                            driverId: driverId,
                            title: gm.Title,
                            description: gm.Description,
                            reward: gm.Reward,
                            target: gm.Target,
                            type: gm.Type,
                            expiresAt: gm.ExpiresAt,
                            globalMissionId: gm.Id);
                        mission.Activate();
                        driversContext.DriverMissions.Add(mission);
                        issuedCount++;
                    }
                }

                await driversContext.SaveChangesAsync();
                logger.LogInformation("Seeded {GlobalMissionCount} global missions and issued {IssuedCount} driver mission records", globalMissions.Length, issuedCount);
            }
            else
            {
                logger.LogInformation("Global missions already seeded, skipping mission seed");
            }

            // Seed Roles
            string[] roles = { "SuperAdmin", "Owner", "Admin", "Dispatcher", "Driver", "Client", "BusinessClient" };
            foreach (var role in roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    var roleResult = await roleManager.CreateAsync(new IdentityRole(role));
                    if (roleResult.Succeeded)
                    {
                        logger.LogInformation("Created role: {Role}", role);
                    }
                    else
                    {
                        logger.LogError("Failed to create role {Role}: {Errors}", role, string.Join(", ", roleResult.Errors.Select(e => e.Description)));
                    }
                }
                else
                {
                    logger.LogInformation("Role already exists: {Role}", role);
                }
            }

            // Interactive admin accounts (superadmin@ / backoffice@) are no longer seeded —
            // admin auth moved to the back-office backend (Features:BackofficeLoginEnabled is off).
            // Service users for S2S clients are still seeded below.

            // Seed service users for S2S clients (e.g. the back-office backend).
            // These act as the identity behind X-Client-Id/X-Api-Key requests so
            // NameIdentifier-based call sites resolve; they can never log in
            // (no password hash, IsActive=false).
            var serviceClients = services.GetService<Microsoft.Extensions.Options.IOptions<BeeLogistics.Shared.Infrastructure.Security.ServiceClientsOptions>>()?.Value;
            foreach (var serviceClient in serviceClients?.Clients ?? new List<BeeLogistics.Shared.Infrastructure.Security.ServiceClientCredential>())
            {
                if (string.IsNullOrWhiteSpace(serviceClient.ServiceUserId))
                {
                    logger.LogWarning("Service client {ClientId} has no ServiceUserId configured; skipping seed", serviceClient.ClientId);
                    continue;
                }

                var existingServiceUser = await userManager.FindByIdAsync(serviceClient.ServiceUserId);
                if (existingServiceUser != null)
                {
                    continue;
                }

                var serviceUser = new ApplicationUser
                {
                    Id = serviceClient.ServiceUserId,
                    UserName = $"svc-{serviceClient.ClientId}@bee.internal",
                    Email = $"svc-{serviceClient.ClientId}@bee.internal",
                    FullName = $"Service: {serviceClient.ClientId}",
                    Role = "SuperAdmin",
                    EmailConfirmed = true,
                    IsActive = false, // never logs in; authenticates via API key only
                    IsOnboarded = true
                };

                var serviceUserResult = await userManager.CreateAsync(serviceUser); // no password
                if (serviceUserResult.Succeeded)
                {
                    await userManager.AddToRoleAsync(serviceUser, "SuperAdmin");
                    logger.LogInformation("Created service user for client: {ClientId}", serviceClient.ClientId);
                }
                else
                {
                    logger.LogError("Failed to create service user for {ClientId}: {Errors}",
                        serviceClient.ClientId, string.Join(", ", serviceUserResult.Errors.Select(e => e.Description)));
                }
            }

            // Single-business "System Admin" account is no longer seeded — admin moved to the back-office backend.

            // Seed FAQ Articles
            var crmContext = services.GetRequiredService<CrmDbContext>();
            if (!await crmContext.FaqArticles.AnyAsync())
            {
                var faqArticles = new[]
                {
                    new FaqArticle
                    {
                        Title = "How do I book a truck?",
                        Content = "To book a truck:\n\n1. Log in to your account\n2. Click 'New Booking' on your dashboard\n3. Enter pickup and dropoff locations\n4. Select truck type and cargo details\n5. Choose your preferred date and time\n6. Review and confirm your booking\n\nYou'll receive a confirmation email with booking details.",
                        Category = "Booking",
                        SortOrder = 1,
                        IsPublished = true,
                        Tags = "booking,truck,how-to"
                    },
                    new FaqArticle
                    {
                        Title = "What payment methods are accepted?",
                        Content = "We accept the following payment methods:\n\n• Credit/Debit Cards (Visa, Mastercard)\n• Bank Transfer\n• E-Wallets (GCash, Maya)\n• Cash on Delivery (for select areas)\n\nAll online payments are processed securely through Xendit.",
                        Category = "Payment",
                        SortOrder = 1,
                        IsPublished = true,
                        Tags = "payment,methods,cards"
                    },
                    new FaqArticle
                    {
                        Title = "How can I track my delivery?",
                        Content = "You can track your delivery in real-time:\n\n1. Go to your dashboard\n2. Find your active booking\n3. Click 'Track' to see live location\n\nYou'll also receive SMS/email updates at key milestones:\n• Driver assigned\n• Pickup completed\n• In transit\n• Arriving soon\n• Delivered",
                        Category = "Tracking",
                        SortOrder = 1,
                        IsPublished = true,
                        Tags = "tracking,delivery,status"
                    },
                    new FaqArticle
                    {
                        Title = "What if I need to cancel my booking?",
                        Content = "Cancellation policy:\n\n• Free cancellation up to 24 hours before pickup\n• 50% charge for cancellations within 24 hours\n• Full charge for no-shows\n\nTo cancel:\n1. Go to your bookings\n2. Select the booking\n3. Click 'Cancel Booking'\n4. Provide a reason\n\nRefunds are processed within 3-5 business days.",
                        Category = "Booking",
                        SortOrder = 2,
                        IsPublished = true,
                        Tags = "cancel,refund,booking"
                    },
                    new FaqArticle
                    {
                        Title = "What truck types are available?",
                        Content = "Available truck types:\n\n• Small Truck (1-2 tons) - Ideal for small moves\n• Medium Truck (3-5 tons) - Furniture, appliances\n• Large Truck (6-10 tons) - Full house moves\n• Flatbed - Construction materials\n• Refrigerated - Perishable goods\n• Container - Long-distance cargo\n\nNot sure which to choose? Contact us for recommendations.",
                        Category = "Services",
                        SortOrder = 1,
                        IsPublished = true,
                        Tags = "trucks,types,capacity"
                    },
                    new FaqArticle
                    {
                        Title = "How do I become a fleet partner?",
                        Content = "Join our fleet partner program:\n\n1. Register as a Business Client\n2. Submit your fleet details\n3. Provide required documents:\n   • Business registration\n   • Vehicle registration\n   • Insurance certificates\n   • Driver licenses\n4. Complete verification (2-3 business days)\n5. Start receiving bookings!\n\nBenefits include dashboard access, real-time tracking, and automated payments.",
                        Category = "Business",
                        SortOrder = 1,
                        IsPublished = true,
                        Tags = "partner,fleet,business,register"
                    }
                };

                crmContext.FaqArticles.AddRange(faqArticles);

                // Seed FAQ Categories
                var faqCategories = new[]
                {
                    new FaqCategory { Name = "Booking", Description = "Questions about making bookings", Icon = "calendar", SortOrder = 1 },
                    new FaqCategory { Name = "Payment", Description = "Payment methods and billing", Icon = "credit-card", SortOrder = 2 },
                    new FaqCategory { Name = "Tracking", Description = "Delivery tracking and updates", Icon = "map-pin", SortOrder = 3 },
                    new FaqCategory { Name = "Services", Description = "Our services and truck types", Icon = "truck", SortOrder = 4 },
                    new FaqCategory { Name = "Business", Description = "Fleet partnership and business accounts", Icon = "briefcase", SortOrder = 5 }
                };

                crmContext.FaqCategories.AddRange(faqCategories);
                await crmContext.SaveChangesAsync();
            }

            // Seed Vehicle Pricing
            var bookingsContext = services.GetRequiredService<BookingsDbContext>();
            var pricingRepository = services.GetRequiredService<BeeLogistics.Modules.Bookings.Application.Interfaces.IVehiclePricingRepository>();
            
            if (!await bookingsContext.VehiclePricings.AnyAsync())
            {
                logger.LogInformation("Seeding vehicle pricing data...");
                
                var vehiclePricings = new[]
                {
                    // Motorcycle
                    new VehiclePricing(
                        vehicleType: "Motorcycle",
                        baseFare: 49m,
                        perKm0to5: 6m,
                        perKmAbove5: 5m,
                        additionalStopFee: 40m,
                        weightLimitKg: 20m,
                        weightSurchargePerKg: 0m,
                        sizeLimit: "0.5x0.4x0.5m",
                        types: "-",
                        surchargeInfo: "High Demand Surcharge of up to 300% may apply during peak hours.",
                        remarks: "The fare of service is based on multiple factors such as traffic situation, order volume, availability of delivery partners, applicable tolls, surcharges, and so on. Hence the total fare of the service may be higher or lower than rates indicated above. The fare displayed at the time of request may not be the same if there is a change to order details. Please refer to our app/web app for real-time accurate pricing."
                    ),
                    // Sedan (200kg Sedan)
                    new VehiclePricing(
                        vehicleType: "Sedan",
                        baseFare: 100m,
                        perKm0to5: 18m,
                        perKmAbove5: 15m,
                        additionalStopFee: 45m,
                        weightLimitKg: 200m,
                        weightSurchargePerKg: 0m,
                        sizeLimit: "1x0.6x0.7m",
                        types: "Hatchback/Sedan",
                        longDistanceBaseFare: 715m,
                        longDistancePerKm41to60: 2m,
                        longDistancePerKmAbove60: 15m,
                        surchargeInfo: "High Demand Surcharge of up to 300% may apply during peak hours.",
                        remarks: "The fare of service is based on multiple factors such as traffic situation, order volume, availability of delivery partners, applicable tolls, surcharges, and so on. Hence the total fare of the service may be higher or lower than rates indicated above. The fare displayed at the time of request may not be the same if there is a change to order details. Please refer to our app/web app for real-time accurate pricing."
                    ),
                    // SUV (300kg Subcompact SUV / Crossover)
                    new VehiclePricing(
                        vehicleType: "SUV",
                        baseFare: 115m,
                        perKm0to5: 20m,
                        perKmAbove5: 17m,
                        additionalStopFee: 45m,
                        weightLimitKg: 300m,
                        weightSurchargePerKg: 0m,
                        sizeLimit: "1.2x1x0.9m",
                        types: "Subcompact SUV / Crossover",
                        longDistanceBaseFare: 885m,
                        longDistancePerKm41to60: 2m,
                        longDistancePerKmAbove60: 17m,
                        surchargeInfo: "High Demand Surcharge of up to 300% may apply during peak hours.",
                        remarks: "The fare of service is based on multiple factors such as traffic situation, order volume, availability of delivery partners, applicable tolls, surcharges, and so on. Hence the total fare of the service may be higher or lower than rates indicated above. The fare displayed at the time of request may not be the same if there is a change to order details. Please refer to our app/web app for real-time accurate pricing."
                    ),
                    // Van (600kg 7-seater SUV / Small Van)
                    new VehiclePricing(
                        vehicleType: "Van",
                        baseFare: 200m,
                        perKm0to5: 20m,
                        perKmAbove5: 17m,
                        additionalStopFee: 50m,
                        weightLimitKg: 600m,
                        weightSurchargePerKg: 0m,
                        sizeLimit: "2.1x1.2x1.1m",
                        types: "7-seater SUV / Small Van",
                        longDistanceBaseFare: 970m,
                        longDistancePerKm41to60: 2m,
                        longDistancePerKmAbove60: 17m,
                        surchargeInfo: "High Demand Surcharge of up to 300% may apply during peak hours.",
                        remarks: "The fare of service is based on multiple factors such as traffic situation, order volume, availability of delivery partners, applicable tolls, surcharges, and so on. Hence the total fare of the service may be higher or lower than rates indicated above. The fare displayed at the time of request may not be the same if there is a change to order details. Please refer to our app/web app for real-time accurate pricing."
                    ),
                    // Pickup (800kg Pickup)
                    new VehiclePricing(
                        vehicleType: "Pickup",
                        baseFare: 240m,
                        perKm0to5: 20m,
                        perKmAbove5: 20m,
                        additionalStopFee: 50m,
                        weightLimitKg: 800m,
                        weightSurchargePerKg: 0m,
                        sizeLimit: "2.7x1.5x0.5m",
                        types: "Pickup",
                        longDistanceBaseFare: 1040m,
                        longDistancePerKm41to60: 2m,
                        longDistancePerKmAbove60: 17m,
                        surchargeInfo: "High Demand Surcharge of up to 300% may apply during peak hours.",
                        remarks: "The fare of service is based on multiple factors such as traffic situation, order volume, availability of delivery partners, applicable tolls, surcharges, and so on. Hence the total fare of the service may be higher or lower than rates indicated above. The fare displayed at the time of request may not be the same if there is a change to order details. Please refer to our app/web app for real-time accurate pricing."
                    ),
                    // L300 (1,000kg L300 / Cargo Van)
                    new VehiclePricing(
                        vehicleType: "L300",
                        baseFare: 280m,
                        perKm0to5: 20m,
                        perKmAbove5: 20m,
                        additionalStopFee: 100m,
                        weightLimitKg: 1000m,
                        weightSurchargePerKg: 0m,
                        sizeLimit: "2.1x1.2x1.2m",
                        types: "L300 / Cargo Van",
                        longDistanceBaseFare: 1080m,
                        longDistancePerKm41to60: 2m,
                        longDistancePerKmAbove60: 18m,
                        surchargeInfo: "High Demand Surcharge of up to 300% may apply during peak hours.",
                        remarks: "The fare of service is based on multiple factors such as traffic situation, order volume, availability of delivery partners, applicable tolls, surcharges, and so on. Hence the total fare of the service may be higher or lower than rates indicated above. The fare displayed at the time of request may not be the same if there is a change to order details. Please refer to our app/web app for real-time accurate pricing."
                    ),
                    // FB2000 (2,000kg FB)
                    new VehiclePricing(
                        vehicleType: "FB2000",
                        baseFare: 900m,
                        perKm0to5: 26m,
                        perKmAbove5: 26m,
                        additionalStopFee: 255m,
                        weightLimitKg: 2000m,
                        weightSurchargePerKg: 0m,
                        sizeLimit: "3x1.7x1.7m",
                        types: "FB",
                        longDistanceBaseFare: 1780m,
                        longDistancePerKm41to60: 5m,
                        longDistancePerKmAbove60: 26m,
                        surchargeInfo: "High Demand Surcharge of up to 300% may apply during peak hours.",
                        remarks: "The fare of service is based on multiple factors such as traffic situation, order volume, availability of delivery partners, applicable tolls, surcharges, and so on. Hence the total fare of the service may be higher or lower than rates indicated above. The fare displayed at the time of request may not be the same if there is a change to order details. Please refer to our app/web app for real-time accurate pricing."
                    ),
                    // Aluminum2000 (2,000kg Aluminum)
                    new VehiclePricing(
                        vehicleType: "Aluminum2000",
                        baseFare: 1040m,
                        perKm0to5: 29m,
                        perKmAbove5: 29m,
                        additionalStopFee: 255m,
                        weightLimitKg: 2000m,
                        weightSurchargePerKg: 0m,
                        sizeLimit: "3x1.7x1.7m",
                        types: "Aluminum",
                        longDistanceBaseFare: 2200m,
                        longDistancePerKm41to60: 5m,
                        longDistancePerKmAbove60: 26m,
                        surchargeInfo: "High Demand Surcharge of up to 300% may apply during peak hours.",
                        remarks: "The fare of service is based on multiple factors such as traffic situation, order volume, availability of delivery partners, applicable tolls, surcharges, and so on. Hence the total fare of the service may be higher or lower than rates indicated above. The fare displayed at the time of request may not be the same if there is a change to order details. Please refer to our app/web app for real-time accurate pricing."
                    ),
                    // Truck3000 (3,000kg Truck)
                    new VehiclePricing(
                        vehicleType: "Truck3000",
                        baseFare: 1450m,
                        perKm0to5: 33m,
                        perKmAbove5: 33m,
                        additionalStopFee: 255m,
                        weightLimitKg: 5000m,
                        weightSurchargePerKg: 0m,
                        sizeLimit: "4.3x1.8x2.1m",
                        types: "Aluminum",
                        longDistanceBaseFare: 2770m,
                        longDistancePerKm41to60: 5m,
                        longDistancePerKmAbove60: 33m,
                        surchargeInfo: "High Demand Surcharge of up to 300% may apply during peak hours.",
                        remarks: "The fare of service is based on multiple factors such as traffic situation, order volume, availability of delivery partners, applicable tolls, surcharges, and so on. Hence the total fare of the service may be higher or lower than rates indicated above. The fare displayed at the time of request may not be the same if there is a change to order details. Please refer to our app/web app for real-time accurate pricing."
                    ),
                    // Truck7000 (7,000kg Truck)
                    new VehiclePricing(
                        vehicleType: "Truck7000",
                        baseFare: 4420m,
                        perKm0to5: 50m,
                        perKmAbove5: 50m,
                        additionalStopFee: 500m,
                        weightLimitKg: 7000m,
                        weightSurchargePerKg: 0m,
                        sizeLimit: "5.5x1.8x0.5m",
                        types: "Aluminum",
                        longDistanceBaseFare: 6420m,
                        longDistancePerKm41to60: 50m,
                        longDistancePerKmAbove60: 50m,
                        surchargeInfo: "High Demand Surcharge of up to 300% may apply during peak hours.",
                        remarks: "The fare of service is based on multiple factors such as traffic situation, order volume, availability of delivery partners, applicable tolls, surcharges, and so on. Hence the total fare of the service may be higher or lower than rates indicated above. The fare displayed at the time of request may not be the same if there is a change to order details. Please refer to our app/web app for real-time accurate pricing."
                    ),
                    // Truck12000 (12,000kg Truck)
                    new VehiclePricing(
                        vehicleType: "Truck12000",
                        baseFare: 7200m,
                        perKm0to5: 85m,
                        perKmAbove5: 85m,
                        additionalStopFee: 800m,
                        weightLimitKg: 12000m,
                        weightSurchargePerKg: 0m,
                        sizeLimit: "10x2.4x2.3m",
                        types: "Aluminum / Wing Van",
                        longDistanceBaseFare: 10600m,
                        longDistancePerKm41to60: 65m,
                        longDistancePerKmAbove60: 60m,
                        surchargeInfo: "High Demand Surcharge of up to 300% may apply during peak hours.",
                        remarks: "The fare of service is based on multiple factors such as traffic situation, order volume, availability of delivery partners, applicable tolls, surcharges, and so on. Hence the total fare of the service may be higher or lower than rates indicated above. The fare displayed at the time of request may not be the same if there is a change to order details. Please refer to our app/web app for real-time accurate pricing."
                    )
                };

                foreach (var pricing in vehiclePricings)
                {
                    pricingRepository.Add(pricing);
                    
                    // Create initial version
                    var initialVersion = new VehiclePricingVersion(
                        pricing.Id,
                        1,
                        pricing.BaseFare,
                        pricing.PerKm0to5,
                        pricing.PerKmAbove5,
                        pricing.AdditionalStopFee,
                        pricing.WeightLimitKg,
                        pricing.WeightSurchargePerKg,
                        pricing.SizeLimit,
                        pricing.Types,
                        pricing.LongDistanceBaseFare,
                        pricing.LongDistancePerKm41to60,
                        pricing.LongDistancePerKmAbove60,
                        pricing.SurchargeInfo,
                        pricing.Remarks,
                        null,
                        "System"
                    );
                    
                    bookingsContext.Set<VehiclePricingVersion>().Add(initialVersion);
                }
                
                await bookingsContext.SaveChangesAsync();
                logger.LogInformation("Seeded {Count} vehicle pricing configurations", vehiclePricings.Length);
            }
            else
            {
                logger.LogInformation("Vehicle pricing data already exists, skipping seed");
            }

            // Note: Fleet module removed - drivers use their own vehicles in the independent-driver model
            // Vehicle pricing is now stored in database instead of appsettings.json

            // Seed Driver Cashbond Config (issue #103) — TEST-ONLY placeholder amounts, so the
            // driver-app flow has something to pay against in dev/staging without an admin having
            // to configure real rates first. Deliberately gated to non-production: a real cashbond
            // amount is a business decision, and seeding ₱10-20 "amounts" into production would be
            // silently wrong rather than merely unhelpful.
            var hostEnvironment = services.GetRequiredService<IHostEnvironment>();
            if (!hostEnvironment.IsProduction())
            {
                // Reuses the driversContext already resolved above for the missions seed — same
                // DI scope, same instance.
                var cashBondConfigRepository = services
                    .GetRequiredService<BeeLogistics.Modules.Drivers.Application.Interfaces.IDriverCashBondConfigRepository>();

                if (!await driversContext.DriverCashBondConfigs.AnyAsync())
                {
                    logger.LogInformation("Seeding test driver cashbond config (non-production only)...");

                    var testAmounts = new (string VehicleType, decimal Amount)[]
                    {
                        ("Motorcycle", 10m),
                        ("Sedan", 11m),
                        ("SUV", 12m),
                        ("Van", 13m),
                        ("Pickup", 14m),
                        ("L300", 15m),
                        ("FB2000", 16m),
                        ("Aluminum2000", 17m),
                        ("Truck3000", 18m),
                        ("Truck7000", 19m),
                        ("Truck12000", 20m),
                    };

                    foreach (var (vehicleType, amount) in testAmounts)
                    {
                        var config = new DriverCashBondConfig(vehicleType, amount, createdByUserName: "Seed (test)");
                        cashBondConfigRepository.Add(config);

                        var initialVersion = new DriverCashBondConfigVersion(
                            config.Id, config.Version, config.Amount, null, "Seed (test)");
                        driversContext.Set<DriverCashBondConfigVersion>().Add(initialVersion);
                    }

                    await driversContext.SaveChangesAsync();
                    logger.LogInformation("Seeded {Count} test driver cashbond configs", testAmounts.Length);
                }
                else
                {
                    logger.LogInformation("Driver cashbond config data already exists, skipping seed");
                }

                // Seed Driver Package-Insurance Fee Config (issue #104) — same test-only
                // placeholder-amount reasoning as the cashbond seed above: without this, the
                // driver-app insurance flow has nothing to pay against until an admin configures
                // real annual premiums. Gated to non-production for the same reason.
                var insuranceFeeConfigRepository = services
                    .GetRequiredService<BeeLogistics.Modules.Drivers.Application.Interfaces.IDriverPackageInsuranceFeeConfigRepository>();

                if (!await driversContext.DriverPackageInsuranceFeeConfigs.AnyAsync())
                {
                    logger.LogInformation("Seeding test driver package-insurance fee config (non-production only)...");

                    var testInsuranceAmounts = new (string VehicleType, decimal Amount)[]
                    {
                        ("Motorcycle", 10m),
                        ("Sedan", 11m),
                        ("SUV", 12m),
                        ("Van", 13m),
                        ("Pickup", 14m),
                        ("L300", 15m),
                        ("FB2000", 16m),
                        ("Aluminum2000", 17m),
                        ("Truck3000", 18m),
                        ("Truck7000", 19m),
                        ("Truck12000", 20m),
                    };

                    foreach (var (vehicleType, amount) in testInsuranceAmounts)
                    {
                        var config = new DriverPackageInsuranceFeeConfig(vehicleType, amount, createdByUserName: "Seed (test)");
                        insuranceFeeConfigRepository.Add(config);

                        var initialVersion = new DriverPackageInsuranceFeeConfigVersion(
                            config.Id, config.Version, config.Amount, null, "Seed (test)");
                        driversContext.Set<DriverPackageInsuranceFeeConfigVersion>().Add(initialVersion);
                    }

                    await driversContext.SaveChangesAsync();
                    logger.LogInformation("Seeded {Count} test driver package-insurance fee configs", testInsuranceAmounts.Length);
                }
                else
                {
                    logger.LogInformation("Driver package-insurance fee config data already exists, skipping seed");
                }
            }
        }
        catch (Exception ex)
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "An error occurred while seeding the database.");
        }
    }
}
