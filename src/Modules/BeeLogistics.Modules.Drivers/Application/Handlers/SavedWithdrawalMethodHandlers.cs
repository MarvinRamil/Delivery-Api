using BeeLogistics.Modules.Drivers.Application.DTOs;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Drivers.Application.Handlers;

// Queries
public record GetSavedWithdrawalMethodsQuery(Guid DriverId) : IRequest<Result<IReadOnlyList<SavedWithdrawalMethodDto>>>;
public record GetSavedWithdrawalMethodByIdQuery(Guid Id, Guid DriverId) : IRequest<Result<SavedWithdrawalMethodDto>>;

// Commands
public record CreateSavedWithdrawalMethodCommand(
    Guid DriverId,
    CreateSavedWithdrawalMethodDto Dto) : IRequest<Result<SavedWithdrawalMethodDto>>;

public record UpdateSavedWithdrawalMethodCommand(
    Guid Id,
    Guid DriverId,
    UpdateSavedWithdrawalMethodDto Dto) : IRequest<Result<SavedWithdrawalMethodDto>>;

public record DeleteSavedWithdrawalMethodCommand(Guid Id, Guid DriverId) : IRequest<Result>;
public record SetDefaultSavedWithdrawalMethodCommand(Guid Id, Guid DriverId) : IRequest<Result<SavedWithdrawalMethodDto>>;

// Handlers
public class GetSavedWithdrawalMethodsHandler(
    ISavedWithdrawalMethodRepository repo,
    ILogger<GetSavedWithdrawalMethodsHandler> logger)
    : IRequestHandler<GetSavedWithdrawalMethodsQuery, Result<IReadOnlyList<SavedWithdrawalMethodDto>>>
{
    public async Task<Result<IReadOnlyList<SavedWithdrawalMethodDto>>> Handle(GetSavedWithdrawalMethodsQuery request, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        logger.LogInformation(
            "[SAVED_WITHDRAWAL_METHODS] [GET_ALL] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Starting - DriverId: {DriverId}",
            startTime, request.DriverId);
        
        try
        {
            var methods = await repo.GetByDriverIdAsync(request.DriverId, ct);
            var dtos = methods.Select(SavedWithdrawalMethodMapper.ToDto).ToList();
            
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogInformation(
                "[SAVED_WITHDRAWAL_METHODS] [GET_ALL] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Success - DriverId: {DriverId}, Count: {Count}, Duration: {Duration}ms",
                DateTime.UtcNow, request.DriverId, dtos.Count, duration);
            
            return Result.Ok<IReadOnlyList<SavedWithdrawalMethodDto>>(dtos);
        }
        catch (Exception ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogError(ex,
                "[SAVED_WITHDRAWAL_METHODS] [GET_ALL] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Error - DriverId: {DriverId}, Duration: {Duration}ms, Exception: {Exception}, StackTrace: {StackTrace}",
                DateTime.UtcNow, request.DriverId, duration, ex.Message, ex.StackTrace);
            return Result.Fail<IReadOnlyList<SavedWithdrawalMethodDto>>("Failed to retrieve saved withdrawal methods");
        }
    }
}

public class GetSavedWithdrawalMethodByIdHandler(
    ISavedWithdrawalMethodRepository repo,
    ILogger<GetSavedWithdrawalMethodByIdHandler> logger)
    : IRequestHandler<GetSavedWithdrawalMethodByIdQuery, Result<SavedWithdrawalMethodDto>>
{
    public async Task<Result<SavedWithdrawalMethodDto>> Handle(GetSavedWithdrawalMethodByIdQuery request, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        logger.LogInformation(
            "[SAVED_WITHDRAWAL_METHODS] [GET_BY_ID] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Starting - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}",
            startTime, request.Id, request.DriverId);
        
        try
        {
            var method = await repo.GetByIdAsync(request.Id, ct);
            
            if (method == null)
            {
                var notFoundDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_WITHDRAWAL_METHODS] [GET_BY_ID] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Not Found - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.DriverId, notFoundDuration);
                return Result.Fail<SavedWithdrawalMethodDto>("Saved withdrawal method not found");
            }
            
            // SECURITY: Verify ownership
            if (method.DriverId != request.DriverId)
            {
                var accessDeniedDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_WITHDRAWAL_METHODS] [GET_BY_ID] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Access Denied - WithdrawalMethodId: {WithdrawalMethodId}, RequestDriverId: {RequestDriverId}, OwnerDriverId: {OwnerDriverId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.DriverId, method.DriverId, accessDeniedDuration);
                return Result.Fail<SavedWithdrawalMethodDto>("Access denied");
            }
            
            var successDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogInformation(
                "[SAVED_WITHDRAWAL_METHODS] [GET_BY_ID] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Success - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, BankName: {BankName}, BankCode: {BankCode}, MaskedAccount: {MaskedAccount}, Duration: {Duration}ms",
                DateTime.UtcNow, request.Id, request.DriverId, method.BankName, method.BankCode, method.GetMaskedAccountNumber(), successDuration);
            
            return Result.Ok(SavedWithdrawalMethodMapper.ToDto(method));
        }
        catch (Exception ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogError(ex,
                "[SAVED_WITHDRAWAL_METHODS] [GET_BY_ID] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Error - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, Duration: {Duration}ms, Exception: {Exception}, StackTrace: {StackTrace}",
                DateTime.UtcNow, request.Id, request.DriverId, duration, ex.Message, ex.StackTrace);
            return Result.Fail<SavedWithdrawalMethodDto>("Failed to retrieve saved withdrawal method");
        }
    }
}

public class CreateSavedWithdrawalMethodHandler(
    ISavedWithdrawalMethodRepository repo,
    ILogger<CreateSavedWithdrawalMethodHandler> logger)
    : IRequestHandler<CreateSavedWithdrawalMethodCommand, Result<SavedWithdrawalMethodDto>>
{
    public async Task<Result<SavedWithdrawalMethodDto>> Handle(CreateSavedWithdrawalMethodCommand request, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        logger.LogInformation(
            "[SAVED_WITHDRAWAL_METHODS] [CREATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Starting - DriverId: {DriverId}, BankName: {BankName}, BankCode: {BankCode}, IsDefault: {IsDefault}",
            startTime, request.DriverId, request.Dto.BankName, request.Dto.BankCode, request.Dto.IsDefault);
        
        try
        {
            // SECURITY: Validate bank code (should be valid Xendit bank code)
            // You may want to add a validation list of allowed bank codes
            
            // If setting as default, unset other defaults first
            if (request.Dto.IsDefault)
            {
                var unsetStart = DateTime.UtcNow;
                await repo.UnsetAllDefaultsAsync(request.DriverId, ct);
                var unsetDuration = (DateTime.UtcNow - unsetStart).TotalMilliseconds;
                logger.LogInformation(
                    "[SAVED_WITHDRAWAL_METHODS] [CREATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Unset Defaults - DriverId: {DriverId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.DriverId, unsetDuration);
            }
            
            // Create domain entity (account number will be encrypted by repository)
            var savedMethod = SavedWithdrawalMethod.Create(
                driverId: request.DriverId,
                bankName: request.Dto.BankName,
                bankCode: request.Dto.BankCode,
                accountNumber: request.Dto.AccountNumber, // Will be encrypted by repository
                accountHolderName: request.Dto.AccountHolderName,
                isDefault: request.Dto.IsDefault
            );
            
            var saveStart = DateTime.UtcNow;
            repo.Add(savedMethod);
            await repo.SaveChangesAsync(ct);
            var saveDuration = (DateTime.UtcNow - saveStart).TotalMilliseconds;
            
            var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogInformation(
                "[SAVED_WITHDRAWAL_METHODS] [CREATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Success - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, BankName: {BankName}, BankCode: {BankCode}, MaskedAccount: {MaskedAccount}, IsDefault: {IsDefault}, SaveDuration: {SaveDuration}ms, TotalDuration: {TotalDuration}ms",
                DateTime.UtcNow, savedMethod.Id, request.DriverId, request.Dto.BankName, request.Dto.BankCode, savedMethod.GetMaskedAccountNumber(), savedMethod.IsDefault, saveDuration, totalDuration);
            
            return Result.Ok(SavedWithdrawalMethodMapper.ToDto(savedMethod));
        }
        catch (ArgumentException ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogWarning(ex,
                "[SAVED_WITHDRAWAL_METHODS] [CREATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Validation Error - DriverId: {DriverId}, Duration: {Duration}ms, Exception: {Exception}",
                DateTime.UtcNow, request.DriverId, duration, ex.Message);
            return Result.Fail<SavedWithdrawalMethodDto>(ex.Message);
        }
        catch (Exception ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogError(ex,
                "[SAVED_WITHDRAWAL_METHODS] [CREATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Error - DriverId: {DriverId}, Duration: {Duration}ms, Exception: {Exception}, StackTrace: {StackTrace}",
                DateTime.UtcNow, request.DriverId, duration, ex.Message, ex.StackTrace);
            return Result.Fail<SavedWithdrawalMethodDto>($"Failed to create saved withdrawal method: {ex.Message}");
        }
    }
}

public class UpdateSavedWithdrawalMethodHandler(
    ISavedWithdrawalMethodRepository repo,
    ILogger<UpdateSavedWithdrawalMethodHandler> logger)
    : IRequestHandler<UpdateSavedWithdrawalMethodCommand, Result<SavedWithdrawalMethodDto>>
{
    public async Task<Result<SavedWithdrawalMethodDto>> Handle(UpdateSavedWithdrawalMethodCommand request, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        logger.LogInformation(
            "[SAVED_WITHDRAWAL_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Starting - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, IsDefault: {IsDefault}",
            startTime, request.Id, request.DriverId, request.Dto.IsDefault?.ToString() ?? "null");
        
        try
        {
            var method = await repo.GetByIdAsync(request.Id, ct);
            
            if (method == null)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_WITHDRAWAL_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Not Found - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.DriverId, duration);
                return Result.Fail<SavedWithdrawalMethodDto>("Saved withdrawal method not found");
            }
            
            // SECURITY: Verify ownership
            if (method.DriverId != request.DriverId)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_WITHDRAWAL_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Access Denied - WithdrawalMethodId: {WithdrawalMethodId}, RequestDriverId: {RequestDriverId}, OwnerDriverId: {OwnerDriverId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.DriverId, method.DriverId, duration);
                return Result.Fail<SavedWithdrawalMethodDto>("Access denied");
            }
            
            // Update bank info if provided
            if (request.Dto.BankName != null || request.Dto.BankCode != null || 
                request.Dto.AccountNumber != null || request.Dto.AccountHolderName != null)
            {
                method.UpdateBankInfo(
                    request.Dto.BankName ?? method.BankName,
                    request.Dto.BankCode ?? method.BankCode,
                    request.Dto.AccountNumber ?? method.AccountNumber, // Will be encrypted by repository
                    request.Dto.AccountHolderName ?? method.AccountHolderName
                );
                logger.LogInformation(
                    "[SAVED_WITHDRAWAL_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Updated Bank Info - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}",
                    DateTime.UtcNow, request.Id, request.DriverId);
            }
            
            // Handle default setting
            if (request.Dto.IsDefault.HasValue)
            {
                if (request.Dto.IsDefault.Value)
                {
                    var unsetStart = DateTime.UtcNow;
                    await repo.UnsetAllDefaultsAsync(request.DriverId, ct);
                    var unsetDuration = (DateTime.UtcNow - unsetStart).TotalMilliseconds;
                    method.SetAsDefault();
                    logger.LogInformation(
                        "[SAVED_WITHDRAWAL_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Set As Default - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, UnsetDuration: {UnsetDuration}ms",
                        DateTime.UtcNow, request.Id, request.DriverId, unsetDuration);
                }
                else
                {
                    method.UnsetAsDefault();
                    logger.LogInformation(
                        "[SAVED_WITHDRAWAL_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Unset As Default - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}",
                        DateTime.UtcNow, request.Id, request.DriverId);
                }
            }
            
            var saveStart = DateTime.UtcNow;
            await repo.SaveChangesAsync(ct);
            var saveDuration = (DateTime.UtcNow - saveStart).TotalMilliseconds;
            
            var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogInformation(
                "[SAVED_WITHDRAWAL_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Success - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, BankName: {BankName}, IsDefault: {IsDefault}, SaveDuration: {SaveDuration}ms, TotalDuration: {TotalDuration}ms",
                DateTime.UtcNow, request.Id, request.DriverId, method.BankName, method.IsDefault, saveDuration, totalDuration);
            
            return Result.Ok(SavedWithdrawalMethodMapper.ToDto(method));
        }
        catch (ArgumentException ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogWarning(ex,
                "[SAVED_WITHDRAWAL_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Validation Error - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, Duration: {Duration}ms, Exception: {Exception}",
                DateTime.UtcNow, request.Id, request.DriverId, duration, ex.Message);
            return Result.Fail<SavedWithdrawalMethodDto>(ex.Message);
        }
        catch (Exception ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogError(ex,
                "[SAVED_WITHDRAWAL_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Error - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, Duration: {Duration}ms, Exception: {Exception}, StackTrace: {StackTrace}",
                DateTime.UtcNow, request.Id, request.DriverId, duration, ex.Message, ex.StackTrace);
            return Result.Fail<SavedWithdrawalMethodDto>($"Failed to update saved withdrawal method: {ex.Message}");
        }
    }
}

public class DeleteSavedWithdrawalMethodHandler(
    ISavedWithdrawalMethodRepository repo,
    ILogger<DeleteSavedWithdrawalMethodHandler> logger)
    : IRequestHandler<DeleteSavedWithdrawalMethodCommand, Result>
{
    public async Task<Result> Handle(DeleteSavedWithdrawalMethodCommand request, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        logger.LogInformation(
            "[SAVED_WITHDRAWAL_METHODS] [DELETE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Starting - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}",
            startTime, request.Id, request.DriverId);
        
        try
        {
            var method = await repo.GetByIdAsync(request.Id, ct);
            
            if (method == null)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_WITHDRAWAL_METHODS] [DELETE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Not Found - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.DriverId, duration);
                return Result.Fail("Saved withdrawal method not found");
            }
            
            // SECURITY: Verify ownership
            if (method.DriverId != request.DriverId)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_WITHDRAWAL_METHODS] [DELETE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Access Denied - WithdrawalMethodId: {WithdrawalMethodId}, RequestDriverId: {RequestDriverId}, OwnerDriverId: {OwnerDriverId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.DriverId, method.DriverId, duration);
                return Result.Fail("Access denied");
            }
            
            // Soft delete
            var saveStart = DateTime.UtcNow;
            method.SoftDelete();
            await repo.SaveChangesAsync(ct);
            var saveDuration = (DateTime.UtcNow - saveStart).TotalMilliseconds;
            
            var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogInformation(
                "[SAVED_WITHDRAWAL_METHODS] [DELETE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Success - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, BankName: {BankName}, MaskedAccount: {MaskedAccount}, SaveDuration: {SaveDuration}ms, TotalDuration: {TotalDuration}ms",
                DateTime.UtcNow, request.Id, request.DriverId, method.BankName, method.GetMaskedAccountNumber(), saveDuration, totalDuration);
            
            return Result.Ok();
        }
        catch (Exception ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogError(ex,
                "[SAVED_WITHDRAWAL_METHODS] [DELETE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Error - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, Duration: {Duration}ms, Exception: {Exception}, StackTrace: {StackTrace}",
                DateTime.UtcNow, request.Id, request.DriverId, duration, ex.Message, ex.StackTrace);
            return Result.Fail($"Failed to delete saved withdrawal method: {ex.Message}");
        }
    }
}

public class SetDefaultSavedWithdrawalMethodHandler(
    ISavedWithdrawalMethodRepository repo,
    ILogger<SetDefaultSavedWithdrawalMethodHandler> logger)
    : IRequestHandler<SetDefaultSavedWithdrawalMethodCommand, Result<SavedWithdrawalMethodDto>>
{
    public async Task<Result<SavedWithdrawalMethodDto>> Handle(SetDefaultSavedWithdrawalMethodCommand request, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        logger.LogInformation(
            "[SAVED_WITHDRAWAL_METHODS] [SET_DEFAULT] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Starting - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}",
            startTime, request.Id, request.DriverId);
        
        try
        {
            var method = await repo.GetByIdAsync(request.Id, ct);
            
            if (method == null)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_WITHDRAWAL_METHODS] [SET_DEFAULT] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Not Found - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.DriverId, duration);
                return Result.Fail<SavedWithdrawalMethodDto>("Saved withdrawal method not found");
            }
            
            // SECURITY: Verify ownership
            if (method.DriverId != request.DriverId)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_WITHDRAWAL_METHODS] [SET_DEFAULT] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Access Denied - WithdrawalMethodId: {WithdrawalMethodId}, RequestDriverId: {RequestDriverId}, OwnerDriverId: {OwnerDriverId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.DriverId, method.DriverId, duration);
                return Result.Fail<SavedWithdrawalMethodDto>("Access denied");
            }
            
            if (!method.IsActive)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_WITHDRAWAL_METHODS] [SET_DEFAULT] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Inactive - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.DriverId, duration);
                return Result.Fail<SavedWithdrawalMethodDto>("Cannot set inactive withdrawal method as default");
            }
            
            // Unset all defaults first
            var unsetStart = DateTime.UtcNow;
            await repo.UnsetAllDefaultsAsync(request.DriverId, ct);
            var unsetDuration = (DateTime.UtcNow - unsetStart).TotalMilliseconds;
            
            // Set as default
            method.SetAsDefault();
            var saveStart = DateTime.UtcNow;
            await repo.SaveChangesAsync(ct);
            var saveDuration = (DateTime.UtcNow - saveStart).TotalMilliseconds;
            
            var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogInformation(
                "[SAVED_WITHDRAWAL_METHODS] [SET_DEFAULT] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Success - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, BankName: {BankName}, UnsetDuration: {UnsetDuration}ms, SaveDuration: {SaveDuration}ms, TotalDuration: {TotalDuration}ms",
                DateTime.UtcNow, request.Id, request.DriverId, method.BankName, unsetDuration, saveDuration, totalDuration);
            
            return Result.Ok(SavedWithdrawalMethodMapper.ToDto(method));
        }
        catch (Exception ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogError(ex,
                "[SAVED_WITHDRAWAL_METHODS] [SET_DEFAULT] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Error - WithdrawalMethodId: {WithdrawalMethodId}, DriverId: {DriverId}, Duration: {Duration}ms, Exception: {Exception}, StackTrace: {StackTrace}",
                DateTime.UtcNow, request.Id, request.DriverId, duration, ex.Message, ex.StackTrace);
            return Result.Fail<SavedWithdrawalMethodDto>($"Failed to set default withdrawal method: {ex.Message}");
        }
    }
}

// Mapper
internal static class SavedWithdrawalMethodMapper
{
    public static SavedWithdrawalMethodDto ToDto(SavedWithdrawalMethod method) => new(
        method.Id,
        method.DriverId,
        method.BankName,
        method.BankCode,
        method.GetMaskedAccountNumber(), // SECURITY: Always mask account number in responses
        method.AccountHolderName,
        method.IsDefault,
        method.IsActive,
        method.LastUsedAt,
        method.CreatedAt,
        method.UpdatedAt
    );
}
