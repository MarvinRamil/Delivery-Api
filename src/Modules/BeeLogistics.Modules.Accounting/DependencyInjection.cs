using BeeLogistics.Modules.Accounting.Application.Consumers;
using BeeLogistics.Modules.Accounting.Application.Interfaces;
using BeeLogistics.Modules.Accounting.Infrastructure;
using BeeLogistics.Modules.Accounting.Infrastructure.Repositories;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeeLogistics.Modules.Accounting;

public static class DependencyInjection
{
    public static IMvcBuilder AddAccountingModule(this IMvcBuilder mvcBuilder, string connectionString)
    {
        var services = mvcBuilder.Services;
        services.AddDbContext<AccountingDbContext>(options =>
            options.UseNpgsql(connectionString, x =>
            {
                x.MigrationsHistoryTable("__AccountingMigrationsHistory", "public");
                x.MigrationsAssembly(typeof(AccountingDbContext).Assembly.GetName().Name);
            }));

        services.AddScoped<ILedgerEntryRepository, LedgerEntryRepository>();
        services.AddScoped<ISalesEntryRepository, SalesEntryRepository>();
        services.AddScoped<IAccountingUnitOfWork, AccountingUnitOfWork>();

        mvcBuilder.AddApplicationPart(typeof(DependencyInjection).Assembly);

        return mvcBuilder;
    }

    public static void AddAccountingConsumers(this IRegistrationConfigurator cfg)
    {
        cfg.AddConsumer<WithdrawalRequestedAccountingConsumer>();
        cfg.AddConsumer<WithdrawalCompletedAccountingConsumer>();
        cfg.AddConsumer<WithdrawalFailedAccountingConsumer>();
        cfg.AddConsumer<SaleRecordedAccountingConsumer>();
        cfg.AddConsumer<PaymentRefundedAccountingConsumer>();
        cfg.AddConsumer<CashSettlementDebitedAccountingConsumer>();
        cfg.AddConsumer<DriverTopUpCreditedAccountingConsumer>();
        cfg.AddConsumer<CashDeficitAdjustedAccountingConsumer>();
    }
}
