using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BeeLogistics.Modules.Revenue.Infrastructure;

/// <summary>
/// Design-time factory for EF Core migrations.
/// Usage: dotnet ef migrations add <Name> --project src/Modules/BeeLogistics.Modules.Revenue
/// </summary>
public class RevenueDbContextFactory : IDesignTimeDbContextFactory<RevenueDbContext>
{
    public RevenueDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<RevenueDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=beelogistics;Username=postgres;Password=postgres",
            x => x.MigrationsHistoryTable("__RevenueMigrationsHistory", "revenue"));
        return new RevenueDbContext(optionsBuilder.Options);
    }
}
