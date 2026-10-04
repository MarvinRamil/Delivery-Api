using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Modules.Fraud.Infrastructure;

public class FraudDbContextFactory : IDesignTimeDbContextFactory<FraudDbContext>
{
    public FraudDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<FraudDbContext>();
        var basePath = Directory.GetCurrentDirectory();
        var apiPath = Path.Combine(basePath, "..", "..", "..", "..", "BeeLogistics.Api");
        if (basePath.EndsWith("BeeLogistics.Api", StringComparison.OrdinalIgnoreCase))
            apiPath = basePath;
        else if (!Directory.Exists(apiPath))
        {
            apiPath = Path.Combine(basePath, "..", "BeeLogistics.Api");
            if (!Directory.Exists(apiPath))
                apiPath = basePath;
        }
        var configuration = new ConfigurationBuilder()
            .SetBasePath(apiPath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        if (string.IsNullOrEmpty(connectionString))
            throw new InvalidOperationException("DefaultConnection not found. Set ConnectionStrings__DefaultConnection.");
        optionsBuilder.UseNpgsql(connectionString, x =>
        {
            x.MigrationsHistoryTable("__FraudMigrationsHistory", "public");
            x.MigrationsAssembly(typeof(FraudDbContext).Assembly.GetName().Name);
        });
        return new FraudDbContext(optionsBuilder.Options);
    }
}
