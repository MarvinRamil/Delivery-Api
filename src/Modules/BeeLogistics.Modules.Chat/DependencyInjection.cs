using BeeLogistics.Modules.Chat.Infrastructure;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Chat;

public static class DependencyInjection
{
    public static IMvcBuilder AddChatModule(this IMvcBuilder mvcBuilder, string connectionString)
    {
        var services = mvcBuilder.Services;

        services.AddDbContext<ChatDbContext>(options =>
            options.UseNpgsql(connectionString, x =>
                x.MigrationsHistoryTable("__ChatMigrationsHistory", "chat")));

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);

        mvcBuilder.AddApplicationPart(typeof(DependencyInjection).Assembly);

        return mvcBuilder;
    }
}
