using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Modules.Offers.Infrastructure;

public class OffersDbContextFactory : IDesignTimeDbContextFactory<OffersDbContext>
{
    public OffersDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");

        var optionsBuilder = new DbContextOptionsBuilder<OffersDbContext>();
        optionsBuilder.UseNpgsql(connectionString, x =>
            x.MigrationsHistoryTable("__OffersMigrationsHistory", "public"));

        return new OffersDbContext(optionsBuilder.Options);
    }
}
