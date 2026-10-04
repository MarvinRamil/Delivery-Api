using BeeLogistics.Modules.Notification.Application.DTOs;
using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Domain;
using BeeLogistics.Shared.Abstractions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Notification.Application.Commands;

public record RegisterDeviceTokenCommand(
    string UserId,
    RegisterDeviceTokenDto Dto
) : IRequest<Result<RegisterDeviceTokenResponse>>;

public class RegisterDeviceTokenCommandHandler : IRequestHandler<RegisterDeviceTokenCommand, Result<RegisterDeviceTokenResponse>>
{
    private readonly IDeviceTokenRepository _repository;
    private readonly IPushProviderPolicy _pushProviderPolicy;

    public RegisterDeviceTokenCommandHandler(
        IDeviceTokenRepository repository,
        IPushProviderPolicy pushProviderPolicy)
    {
        _repository = repository;
        _pushProviderPolicy = pushProviderPolicy;
    }

    public async Task<Result<RegisterDeviceTokenResponse>> Handle(RegisterDeviceTokenCommand request, CancellationToken ct)
    {
        // Vendor-sync hint (Part A5): tell the client the token type we currently expect.
        var expectedTokenType = _pushProviderPolicy.ExpectedTokenType(request.Dto.Platform);

        // Matches the unique index (Token alone, active or not), so a token that exists in any
        // state is found here rather than colliding at the INSERT below.
        var existingToken = await _repository.GetByTokenAsync(request.Dto.DeviceToken, ct);

        if (existingToken != null)
        {
            // Update existing token
            existingToken.UserId = request.UserId;
            existingToken.Platform = request.Dto.Platform;
            existingToken.AppType = request.Dto.AppType;
            existingToken.TokenType = request.Dto.TokenType;
            existingToken.LastUsedAt = DateTime.UtcNow;
            existingToken.IsActive = true;

            await _repository.UpdateAsync(existingToken, ct);
            await _repository.SaveChangesAsync(ct);

            return Result.Ok(new RegisterDeviceTokenResponse(true, "Device token updated successfully", expectedTokenType));
        }

        // Create new token
        var deviceToken = new DeviceToken(
            request.UserId,
            request.Dto.DeviceToken,
            request.Dto.Platform,
            request.Dto.AppType,
            request.Dto.TokenType
        );

        try
        {
            await _repository.AddAsync(deviceToken, ct);
            await _repository.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Another registration of the same token landed between our lookup and this insert.
            // The app registers on launch and on token refresh, so two in flight at once is
            // ordinary rather than exotic. The unique index is the real guard; the lookup above is
            // only an optimisation, so a collision here means the other request won and its row is
            // the one to keep.
            //
            // Detach first: the failed entity is still tracked as Added, so the update below would
            // otherwise re-attempt the very INSERT that just failed.
            foreach (var entry in ex.Entries)
                entry.State = EntityState.Detached;

            var winner = await _repository.GetByTokenAsync(request.Dto.DeviceToken, ct);
            if (winner is null) throw;

            winner.UserId = request.UserId;
            winner.Platform = request.Dto.Platform;
            winner.AppType = request.Dto.AppType;
            winner.TokenType = request.Dto.TokenType;
            winner.LastUsedAt = DateTime.UtcNow;
            winner.IsActive = true;

            await _repository.UpdateAsync(winner, ct);
            await _repository.SaveChangesAsync(ct);

            return Result.Ok(new RegisterDeviceTokenResponse(true, "Device token updated successfully", expectedTokenType));
        }

        return Result.Ok(new RegisterDeviceTokenResponse(true, "Device token registered successfully", expectedTokenType));
    }
}

