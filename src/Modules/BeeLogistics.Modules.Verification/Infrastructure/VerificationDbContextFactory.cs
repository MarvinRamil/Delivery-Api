using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Modules.Verification.Infrastructure;

public class VerificationDbContextFactory : IDesignTimeDbContextFactory<VerificationDbContext>
{
    public VerificationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<VerificationDbContext>();

        // Build configuration from appsettings.json (in the API project)
        var basePath = Directory.GetCurrentDirectory();
        var apiPath = Path.Combine(basePath, "..", "..", "..", "..", "BeeLogistics.Api");

        if (basePath.EndsWith("BeeLogistics.Api", StringComparison.OrdinalIgnoreCase))
        {
            apiPath = basePath;
        }
        else if (!Directory.Exists(apiPath))
        {
            apiPath = Path.Combine(basePath, "..", "BeeLogistics.Api");
            if (!Directory.Exists(apiPath))
            {
                apiPath = basePath;
            }
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(apiPath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development"}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");

        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException(
                "DefaultConnection connection string not found. " +
                "Please set ConnectionStrings__DefaultConnection in appsettings.json or as an environment variable.");
        }

        optionsBuilder.UseNpgsql(connectionString, x => x.MigrationsHistoryTable("__VerificationMigrationsHistory", "public"));

        return new VerificationDbContext(optionsBuilder.Options);
    }
}
