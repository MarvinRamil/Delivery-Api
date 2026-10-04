using BeeLogistics.Modules.Drivers.Infrastructure;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Drivers.Application.Consumers;

/// <summary>
/// Consumes the UserDeletedIntegrationEvent to perform data erasure (anonymization) 
/// on driver-specific data (Applications, Wallets) in compliance with the PH Data Privacy Act of 2012.
/// </summary>
public class UserDeletedIntegrationEventConsumer : IConsumer<IUserDeletedIntegrationEvent>
{
    private readonly DriversDbContext _dbContext;
    private readonly ILogger<UserDeletedIntegrationEventConsumer> _logger;

    public UserDeletedIntegrationEventConsumer(
        DriversDbContext dbContext,
        ILogger<UserDeletedIntegrationEventConsumer> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<IUserDeletedIntegrationEvent> context)
    {
        var msg = context.Message;
        _logger.LogInformation("Processing data erasure for deleted user {UserId} in Drivers module.", msg.UserId);

        try
        {
            // 1. Wipe Driver Applications
            var applications = await _dbContext.DriverApplications
                .Where(a => a.UserId == msg.UserId)
                .ToListAsync(context.CancellationToken);

            foreach (var app in applications)
            {
                app.WipeData(msg.FullName);
            }

            // 2. Wipe Driver Wallet bank details
            var wallet = await _dbContext.DriverWallets
                .FirstOrDefaultAsync(w => w.DriverId == Guid.Parse(msg.UserId), context.CancellationToken);

            if (wallet != null)
            {
                wallet.UpdateBankDetails("DELETED", "DELETED", "DELETED");
                _logger.LogInformation("Driver wallet details wiped for user {UserId}", msg.UserId);
            }

            await _dbContext.SaveChangesAsync(context.CancellationToken);
            _logger.LogInformation("Successfully completed data erasure for user {UserId} in Drivers module.", msg.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to perform data erasure for user {UserId} in Drivers module.", msg.UserId);
            throw; // MassTransit will retry based on policy
        }
    }
}
