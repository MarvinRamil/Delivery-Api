using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Modules.Drivers.Infrastructure;

public class DriversDbContextFactory : IDesignTimeDbContextFactory<DriversDbContext>
{
    public DriversDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DriversDbContext>();
        
        // Build configuration from appsettings.json (in the API project)
        // Try multiple paths to find appsettings.json
        var basePath = Directory.GetCurrentDirectory();
        var apiPath = Path.Combine(basePath, "..", "..", "..", "..", "BeeLogistics.Api");
        
        // If running from API project, use current directory
        if (basePath.EndsWith("BeeLogistics.Api", StringComparison.OrdinalIgnoreCase))
        {
            apiPath = basePath;
        }
        // If path doesn't exist, try alternative paths
        else if (!Directory.Exists(apiPath))
        {
            apiPath = Path.Combine(basePath, "..", "BeeLogistics.Api");
            if (!Directory.Exists(apiPath))
            {
                apiPath = basePath; // Fallback to current directory
            }
        }
        
        var configuration = new ConfigurationBuilder()
            .SetBasePath(apiPath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development"}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        
        // Get connection string from configuration (prioritizes environment variables, then appsettings.json)
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException(
                "DefaultConnection connection string not found. " +
                "Please set ConnectionStrings__DefaultConnection in appsettings.json or as an environment variable.");
        }
        
        optionsBuilder.UseNpgsql(connectionString, x => x.MigrationsHistoryTable("__DriversMigrationsHistory", "public"));
        
        return new DriversDbContext(optionsBuilder.Options);
    }
}

