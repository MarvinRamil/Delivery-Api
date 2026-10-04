using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Modules.Giveaways.Infrastructure;

public class GiveawaysDbContextFactory : IDesignTimeDbContextFactory<GiveawaysDbContext>
{
    public GiveawaysDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");

        var optionsBuilder = new DbContextOptionsBuilder<GiveawaysDbContext>();
        optionsBuilder.UseNpgsql(connectionString, x =>
            x.MigrationsHistoryTable("__GiveawaysMigrationsHistory", "public"));

        return new GiveawaysDbContext(optionsBuilder.Options);
    }
}
