using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Modules.Messaging.Infrastructure;

/// <summary>
/// Design-time factory so `dotnet ef migrations add` works without booting the API. Reads the same
/// connection string the app does, from the API project's configuration.
/// </summary>
public class MessagingDbContextFactory : IDesignTimeDbContextFactory<MessagingDbContext>
{
    public MessagingDbContext CreateDbContext(string[] args)
    {
        var basePath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "BeeLogistics.Api");
        if (!Directory.Exists(basePath))
            basePath = Directory.GetCurrentDirectory();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.GetFullPath(basePath))
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? "Host=localhost;Port=5432;Database=bee_logistics;Username=postgres;Password=postgres";

        var builder = new DbContextOptionsBuilder<MessagingDbContext>();
        builder.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__MessagingMigrationsHistory", MessagingDbContext.Schema));

        return new MessagingDbContext(builder.Options);
    }
}
