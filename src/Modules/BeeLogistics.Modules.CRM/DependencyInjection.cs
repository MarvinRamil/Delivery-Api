using BeeLogistics.Modules.CRM.Infrastructure;
using BeeLogistics.Modules.CRM.Infrastructure.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BeeLogistics.Modules.CRM;

public static class DependencyInjection
{
    public static IMvcBuilder AddCrmModule(this IMvcBuilder mvcBuilder, string connectionString, IConfiguration configuration)
    {
        var services = mvcBuilder.Services;

        services.AddDbContext<CrmDbContext>(options =>
            options.UseNpgsql(connectionString, x =>
                x.MigrationsHistoryTable("__CrmMigrationsHistory", "crm")));

        // Zammad helpdesk integration
        services.Configure<ZammadSettings>(
            configuration.GetSection(ZammadSettings.SectionName));
        services.AddHttpClient<ZammadService>();
        services.AddScoped<ZammadTicketSyncService>();

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);

        mvcBuilder.AddApplicationPart(typeof(DependencyInjection).Assembly);

        return mvcBuilder;
    }
}
