using BeeLogistics.Modules.Payment.Application.DTOs;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Payment.Application.Handlers;

// Queries
public record GetSavedPaymentMethodsQuery(Guid CustomerId) : IRequest<Result<IReadOnlyList<SavedPaymentMethodDto>>>;
public record GetSavedPaymentMethodByIdQuery(Guid Id, Guid CustomerId) : IRequest<Result<SavedPaymentMethodDto>>;

// Commands
public record CreateSavedPaymentMethodCommand(
    Guid CustomerId,
    CreateSavedPaymentMethodDto Dto,
    string CustomerEmail,
    string CustomerFullName) : IRequest<Result<SavedPaymentMethodDto>>;

public record UpdateSavedPaymentMethodCommand(
    Guid Id,
    Guid CustomerId,
    UpdateSavedPaymentMethodDto Dto) : IRequest<Result<SavedPaymentMethodDto>>;

public record DeleteSavedPaymentMethodCommand(Guid Id, Guid CustomerId) : IRequest<Result>;
public record SetDefaultSavedPaymentMethodCommand(Guid Id, Guid CustomerId) : IRequest<Result<SavedPaymentMethodDto>>;

// Handlers
public class GetSavedPaymentMethodsHandler(
    ISavedPaymentMethodRepository repo,
    ILogger<GetSavedPaymentMethodsHandler> logger) 
    : IRequestHandler<GetSavedPaymentMethodsQuery, Result<IReadOnlyList<SavedPaymentMethodDto>>>
{
    public async Task<Result<IReadOnlyList<SavedPaymentMethodDto>>> Handle(GetSavedPaymentMethodsQuery request, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        logger.LogInformation(
            "[SAVED_PAYMENT_METHODS] [GET_ALL] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Starting - CustomerId: {CustomerId}",
            startTime, request.CustomerId);
        
        try
        {
            var methods = await repo.GetByCustomerIdAsync(request.CustomerId, ct);
            var dtos = methods.Select(SavedPaymentMethodMapper.ToDto).ToList();
            
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogInformation(
                "[SAVED_PAYMENT_METHODS] [GET_ALL] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Success - CustomerId: {CustomerId}, Count: {Count}, Duration: {Duration}ms",
                DateTime.UtcNow, request.CustomerId, dtos.Count, duration);
            
            return Result.Ok<IReadOnlyList<SavedPaymentMethodDto>>(dtos);
        }
        catch (Exception ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogError(ex,
                "[SAVED_PAYMENT_METHODS] [GET_ALL] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Error - CustomerId: {CustomerId}, Duration: {Duration}ms, Exception: {Exception}",
                DateTime.UtcNow, request.CustomerId, duration, ex.Message);
            return Result.Fail<IReadOnlyList<SavedPaymentMethodDto>>("Failed to retrieve saved payment methods");
        }
    }
}

public class GetSavedPaymentMethodByIdHandler(
    ISavedPaymentMethodRepository repo,
    ILogger<GetSavedPaymentMethodByIdHandler> logger)
    : IRequestHandler<GetSavedPaymentMethodByIdQuery, Result<SavedPaymentMethodDto>>
{
    public async Task<Result<SavedPaymentMethodDto>> Handle(GetSavedPaymentMethodByIdQuery request, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        logger.LogInformation(
            "[SAVED_PAYMENT_METHODS] [GET_BY_ID] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Starting - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}",
            startTime, request.Id, request.CustomerId);
        
        try
        {
            var method = await repo.GetByIdAsync(request.Id, ct);
            
            if (method == null)
            {
                var notFoundDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_PAYMENT_METHODS] [GET_BY_ID] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Not Found - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.CustomerId, notFoundDuration);
                return Result.Fail<SavedPaymentMethodDto>("Saved payment method not found");
            }
            
            // SECURITY: Verify ownership
            if (method.CustomerId != request.CustomerId)
            {
                var accessDeniedDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_PAYMENT_METHODS] [GET_BY_ID] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Access Denied - PaymentMethodId: {PaymentMethodId}, RequestCustomerId: {RequestCustomerId}, OwnerCustomerId: {OwnerCustomerId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.CustomerId, method.CustomerId, accessDeniedDuration);
                return Result.Fail<SavedPaymentMethodDto>("Access denied");
            }
            
            var successDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogInformation(
                "[SAVED_PAYMENT_METHODS] [GET_BY_ID] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Success - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}, Type: {Type}, Last4: {Last4}, Duration: {Duration}ms",
                DateTime.UtcNow, request.Id, request.CustomerId, method.Type, method.Last4Digits, successDuration);
            
            return Result.Ok(SavedPaymentMethodMapper.ToDto(method));
        }
        catch (Exception ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogError(ex,
                "[SAVED_PAYMENT_METHODS] [GET_BY_ID] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Error - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}, Duration: {Duration}ms, Exception: {Exception}",
                DateTime.UtcNow, request.Id, request.CustomerId, duration, ex.Message);
            return Result.Fail<SavedPaymentMethodDto>("Failed to retrieve saved payment method");
        }
    }
}

public class CreateSavedPaymentMethodHandler(
    ISavedPaymentMethodRepository repo,
    IPaymentGatewayFactory gatewayFactory,
    ILogger<CreateSavedPaymentMethodHandler> logger)
    : IRequestHandler<CreateSavedPaymentMethodCommand, Result<SavedPaymentMethodDto>>
{
    public async Task<Result<SavedPaymentMethodDto>> Handle(CreateSavedPaymentMethodCommand request, CancellationToken ct)
    {
        // New saved methods vault with the active gateway; the token the client
        // sends must come from the matching provider SDK (Xendit.js / PayMongo.js).
        var gateway = gatewayFactory.GetActive();
        var startTime = DateTime.UtcNow;
        logger.LogInformation(
            "[SAVED_PAYMENT_METHODS] [CREATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Starting - CustomerId: {CustomerId}, XenditPaymentMethodId: {XenditPaymentMethodId}, Type: {Type}, IsDefault: {IsDefault}",
            startTime, request.CustomerId, request.Dto.XenditPaymentMethodId, request.Dto.Type, request.Dto.IsDefault);
        
        try
        {
            // SECURITY: Check if payment method already exists (prevent duplicates)
            var existing = await repo.GetByProviderPaymentMethodIdAsync(gateway.ProviderName, request.Dto.XenditPaymentMethodId, ct);
            if (existing != null)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_PAYMENT_METHODS] [CREATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Duplicate - CustomerId: {CustomerId}, XenditPaymentMethodId: {XenditPaymentMethodId}, ExistingId: {ExistingId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.CustomerId, request.Dto.XenditPaymentMethodId, existing.Id, duration);
                return Result.Fail<SavedPaymentMethodDto>("This payment method is already saved");
            }
            
            // Get or create the provider customer
            string providerCustomerId;
            var customerCheckStart = DateTime.UtcNow;
            var existingCustomer = await gateway.GetCustomerAsync(request.CustomerId.ToString(), ct);
            var customerCheckDuration = (DateTime.UtcNow - customerCheckStart).TotalMilliseconds;

            if (existingCustomer == null)
            {
                var customerRequest = new CreateGatewayCustomerRequest(
                    ReferenceId: request.CustomerId.ToString(),
                    Email: request.CustomerEmail,
                    GivenNames: request.CustomerFullName.Split(' ').FirstOrDefault() ?? request.CustomerFullName,
                    Surname: request.CustomerFullName.Split(' ').Skip(1).FirstOrDefault()
                );

                var customerCreateStart = DateTime.UtcNow;
                var providerCustomer = await gateway.CreateCustomerAsync(customerRequest, ct);
                var customerCreateDuration = (DateTime.UtcNow - customerCreateStart).TotalMilliseconds;
                providerCustomerId = providerCustomer.Id;

                logger.LogInformation(
                    "[SAVED_PAYMENT_METHODS] [CREATE] [PROVIDER_CUSTOMER] Created - Gateway: {Gateway}, CustomerId: {CustomerId}, ProviderCustomerId: {ProviderCustomerId}, Duration: {Duration}ms",
                    gateway.ProviderName, request.CustomerId, providerCustomerId, customerCreateDuration);
            }
            else
            {
                providerCustomerId = existingCustomer.Id;
                logger.LogInformation(
                    "[SAVED_PAYMENT_METHODS] [CREATE] [PROVIDER_CUSTOMER] Found Existing - Gateway: {Gateway}, CustomerId: {CustomerId}, ProviderCustomerId: {ProviderCustomerId}, Duration: {Duration}ms",
                    gateway.ProviderName, request.CustomerId, providerCustomerId, customerCheckDuration);
            }

            // Get payment method details from the provider to populate display info
            var paymentMethodCheckStart = DateTime.UtcNow;
            var providerPaymentMethod = await gateway.GetPaymentMethodAsync(request.Dto.XenditPaymentMethodId, ct);
            var paymentMethodCheckDuration = (DateTime.UtcNow - paymentMethodCheckStart).TotalMilliseconds;

            if (providerPaymentMethod == null)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_PAYMENT_METHODS] [CREATE] [PROVIDER_PAYMENT_METHOD] Not Found - Gateway: {Gateway}, CustomerId: {CustomerId}, Duration: {Duration}ms",
                    gateway.ProviderName, request.CustomerId, duration);
                return Result.Fail<SavedPaymentMethodDto>("Payment method not found at the payment provider");
            }

            logger.LogInformation(
                "[SAVED_PAYMENT_METHODS] [CREATE] [PROVIDER_PAYMENT_METHOD] Retrieved - Gateway: {Gateway}, CustomerId: {CustomerId}, Type: {Type}, Status: {Status}, Duration: {Duration}ms",
                gateway.ProviderName, request.CustomerId, providerPaymentMethod.Type, providerPaymentMethod.Status, paymentMethodCheckDuration);

            // Extract card details if available
            string last4Digits = request.Dto.Last4Digits;
            string? cardBrand = request.Dto.CardBrand;
            int? expiryMonth = request.Dto.ExpiryMonth;
            int? expiryYear = request.Dto.ExpiryYear;

            if (providerPaymentMethod.Card != null)
            {
                last4Digits = providerPaymentMethod.Card.Last4;
                cardBrand = providerPaymentMethod.Card.Brand;
                expiryMonth = providerPaymentMethod.Card.ExpiryMonth;
                expiryYear = providerPaymentMethod.Card.ExpiryYear;
            }
            
            // Map DTO type to domain type
            var domainType = request.Dto.Type switch
            {
                PaymentMethodTypeDto.CreditCard => PaymentMethodType.CreditCard,
                PaymentMethodTypeDto.DebitCard => PaymentMethodType.DebitCard,
                PaymentMethodTypeDto.EWallet => PaymentMethodType.EWallet,
                _ => PaymentMethodType.CreditCard
            };
            
            // If setting as default, unset other defaults first
            if (request.Dto.IsDefault)
            {
                await repo.UnsetAllDefaultsAsync(request.CustomerId, ct);
            }
            
            // Create domain entity
            var savedMethod = SavedPaymentMethod.Create(
                customerId: request.CustomerId,
                provider: gateway.ProviderName,
                providerCustomerId: providerCustomerId,
                providerPaymentMethodId: request.Dto.XenditPaymentMethodId,
                type: domainType,
                last4Digits: last4Digits,
                cardBrand: cardBrand,
                expiryMonth: expiryMonth,
                expiryYear: expiryYear,
                cardholderName: request.Dto.CardholderName,
                isDefault: request.Dto.IsDefault
            );
            
            var saveStart = DateTime.UtcNow;
            repo.Add(savedMethod);
            await repo.SaveChangesAsync(ct);
            var saveDuration = (DateTime.UtcNow - saveStart).TotalMilliseconds;
            
            var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var maskedToken = string.IsNullOrEmpty(request.Dto.XenditPaymentMethodId) 
                ? "null" 
                : request.Dto.XenditPaymentMethodId[..Math.Min(10, request.Dto.XenditPaymentMethodId.Length)] + "********";

            logger.LogInformation(
                "[SAVED_PAYMENT_METHODS] [CREATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Success - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}, Type: {Type}, Last4: {Last4}, XenditToken: {MaskedToken}, IsDefault: {IsDefault}, TotalDuration: {TotalDuration}ms",
                DateTime.UtcNow, savedMethod.Id, request.CustomerId, savedMethod.Type, savedMethod.Last4Digits, maskedToken, savedMethod.IsDefault, totalDuration);
            
            return Result.Ok(SavedPaymentMethodMapper.ToDto(savedMethod));
        }
        catch (Exception ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var maskedToken = string.IsNullOrEmpty(request.Dto.XenditPaymentMethodId) 
                ? "null" 
                : request.Dto.XenditPaymentMethodId[..Math.Min(10, request.Dto.XenditPaymentMethodId.Length)] + "********";

            logger.LogError(ex,
                "[SAVED_PAYMENT_METHODS] [CREATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Error - CustomerId: {CustomerId}, XenditPaymentMethodId: {MaskedToken}, Duration: {Duration}ms, Exception: {Exception}",
                DateTime.UtcNow, request.CustomerId, maskedToken, duration, ex.Message);
            return Result.Fail<SavedPaymentMethodDto>($"Failed to create saved payment method: {ex.Message}");
        }
    }
}

public class UpdateSavedPaymentMethodHandler(
    ISavedPaymentMethodRepository repo,
    ILogger<UpdateSavedPaymentMethodHandler> logger)
    : IRequestHandler<UpdateSavedPaymentMethodCommand, Result<SavedPaymentMethodDto>>
{
    public async Task<Result<SavedPaymentMethodDto>> Handle(UpdateSavedPaymentMethodCommand request, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        logger.LogInformation(
            "[SAVED_PAYMENT_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Starting - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}, IsDefault: {IsDefault}",
            startTime, request.Id, request.CustomerId, request.Dto.IsDefault?.ToString() ?? "null");
        
        try
        {
            var method = await repo.GetByIdAsync(request.Id, ct);
            
            if (method == null)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_PAYMENT_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Not Found - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.CustomerId, duration);
                return Result.Fail<SavedPaymentMethodDto>("Saved payment method not found");
            }
            
            // SECURITY: Verify ownership
            if (method.CustomerId != request.CustomerId)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_PAYMENT_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Access Denied - PaymentMethodId: {PaymentMethodId}, RequestCustomerId: {RequestCustomerId}, OwnerCustomerId: {OwnerCustomerId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.CustomerId, method.CustomerId, duration);
                return Result.Fail<SavedPaymentMethodDto>("Access denied");
            }
            
            // Update display info if provided
            if (request.Dto.CardholderName != null || request.Dto.ExpiryMonth.HasValue || request.Dto.ExpiryYear.HasValue)
            {
                method.UpdateDisplayInfo(
                    request.Dto.CardholderName,
                    request.Dto.ExpiryMonth,
                    request.Dto.ExpiryYear
                );
                logger.LogInformation(
                    "[SAVED_PAYMENT_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Updated Display Info - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}",
                    DateTime.UtcNow, request.Id, request.CustomerId);
            }
            
            // Handle default setting
            if (request.Dto.IsDefault.HasValue)
            {
                if (request.Dto.IsDefault.Value)
                {
                    await repo.UnsetAllDefaultsAsync(request.CustomerId, ct);
                    method.SetAsDefault();
                    logger.LogInformation(
                        "[SAVED_PAYMENT_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Set As Default - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}",
                        DateTime.UtcNow, request.Id, request.CustomerId);
                }
                else
                {
                    method.UnsetAsDefault();
                    logger.LogInformation(
                        "[SAVED_PAYMENT_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Unset As Default - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}",
                        DateTime.UtcNow, request.Id, request.CustomerId);
                }
            }
            
            var saveStart = DateTime.UtcNow;
            await repo.SaveChangesAsync(ct);
            var saveDuration = (DateTime.UtcNow - saveStart).TotalMilliseconds;
            
            var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogInformation(
                "[SAVED_PAYMENT_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Success - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}, IsDefault: {IsDefault}, SaveDuration: {SaveDuration}ms, TotalDuration: {TotalDuration}ms",
                DateTime.UtcNow, request.Id, request.CustomerId, method.IsDefault, saveDuration, totalDuration);
            
            return Result.Ok(SavedPaymentMethodMapper.ToDto(method));
        }
        catch (Exception ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogError(ex,
                "[SAVED_PAYMENT_METHODS] [UPDATE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Error - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}, Duration: {Duration}ms, Exception: {Exception}, StackTrace: {StackTrace}",
                DateTime.UtcNow, request.Id, request.CustomerId, duration, ex.Message, ex.StackTrace);
            return Result.Fail<SavedPaymentMethodDto>($"Failed to update saved payment method: {ex.Message}");
        }
    }
}

public class DeleteSavedPaymentMethodHandler(
    ISavedPaymentMethodRepository repo,
    IPaymentGatewayFactory gatewayFactory,
    ILogger<DeleteSavedPaymentMethodHandler> logger)
    : IRequestHandler<DeleteSavedPaymentMethodCommand, Result>
{
    public async Task<Result> Handle(DeleteSavedPaymentMethodCommand request, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        logger.LogInformation(
            "[SAVED_PAYMENT_METHODS] [DELETE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Starting - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}",
            startTime, request.Id, request.CustomerId);
        
        try
        {
            var method = await repo.GetByIdAsync(request.Id, ct);
            
            if (method == null)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_PAYMENT_METHODS] [DELETE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Not Found - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.CustomerId, duration);
                return Result.Fail("Saved payment method not found");
            }
            
            // SECURITY: Verify ownership
            if (method.CustomerId != request.CustomerId)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_PAYMENT_METHODS] [DELETE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Access Denied - PaymentMethodId: {PaymentMethodId}, RequestCustomerId: {RequestCustomerId}, OwnerCustomerId: {OwnerCustomerId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.CustomerId, method.CustomerId, duration);
                return Result.Fail("Access denied");
            }
            
            // Delete at the provider that vaulted the token (best-effort; failure is not fatal).
            // Existing records route by their stored provider, not the active gateway.
            try
            {
                var gateway = gatewayFactory.Get(method.Provider);
                var providerDeleteStart = DateTime.UtcNow;
                await gateway.DeletePaymentMethodAsync(method.ProviderPaymentMethodId, ct);
                var providerDeleteDuration = (DateTime.UtcNow - providerDeleteStart).TotalMilliseconds;
                logger.LogInformation(
                    "[SAVED_PAYMENT_METHODS] [DELETE] [PROVIDER] Deleted - Gateway: {Gateway}, PaymentMethodId: {PaymentMethodId}, Duration: {Duration}ms",
                    gateway.ProviderName, request.Id, providerDeleteDuration);
            }
            catch (Exception ex)
            {
                // Log but don't fail - provider-side deletion is optional
                logger.LogWarning(ex,
                    "[SAVED_PAYMENT_METHODS] [DELETE] [PROVIDER] Failed - PaymentMethodId: {PaymentMethodId}, Exception: {Exception}",
                    request.Id, ex.Message);
            }
            
            // Soft delete
            var saveStart = DateTime.UtcNow;
            method.SoftDelete();
            await repo.SaveChangesAsync(ct);
            var saveDuration = (DateTime.UtcNow - saveStart).TotalMilliseconds;
            
            var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogInformation(
                "[SAVED_PAYMENT_METHODS] [DELETE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Success - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}, Type: {Type}, Last4: {Last4}, SaveDuration: {SaveDuration}ms, TotalDuration: {TotalDuration}ms",
                DateTime.UtcNow, request.Id, request.CustomerId, method.Type, method.Last4Digits, saveDuration, totalDuration);
            
            return Result.Ok();
        }
        catch (Exception ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogError(ex,
                "[SAVED_PAYMENT_METHODS] [DELETE] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Error - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}, Duration: {Duration}ms, Exception: {Exception}, StackTrace: {StackTrace}",
                DateTime.UtcNow, request.Id, request.CustomerId, duration, ex.Message, ex.StackTrace);
            return Result.Fail($"Failed to delete saved payment method: {ex.Message}");
        }
    }
}

public class SetDefaultSavedPaymentMethodHandler(
    ISavedPaymentMethodRepository repo,
    ILogger<SetDefaultSavedPaymentMethodHandler> logger)
    : IRequestHandler<SetDefaultSavedPaymentMethodCommand, Result<SavedPaymentMethodDto>>
{
    public async Task<Result<SavedPaymentMethodDto>> Handle(SetDefaultSavedPaymentMethodCommand request, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        logger.LogInformation(
            "[SAVED_PAYMENT_METHODS] [SET_DEFAULT] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Starting - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}",
            startTime, request.Id, request.CustomerId);
        
        try
        {
            var method = await repo.GetByIdAsync(request.Id, ct);
            
            if (method == null)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_PAYMENT_METHODS] [SET_DEFAULT] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Not Found - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.CustomerId, duration);
                return Result.Fail<SavedPaymentMethodDto>("Saved payment method not found");
            }
            
            // SECURITY: Verify ownership
            if (method.CustomerId != request.CustomerId)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_PAYMENT_METHODS] [SET_DEFAULT] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Access Denied - PaymentMethodId: {PaymentMethodId}, RequestCustomerId: {RequestCustomerId}, OwnerCustomerId: {OwnerCustomerId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.CustomerId, method.CustomerId, duration);
                return Result.Fail<SavedPaymentMethodDto>("Access denied");
            }
            
            if (!method.IsActive)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                logger.LogWarning(
                    "[SAVED_PAYMENT_METHODS] [SET_DEFAULT] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Inactive - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}, Duration: {Duration}ms",
                    DateTime.UtcNow, request.Id, request.CustomerId, duration);
                return Result.Fail<SavedPaymentMethodDto>("Cannot set inactive payment method as default");
            }
            
            // Unset all defaults first
            var unsetStart = DateTime.UtcNow;
            await repo.UnsetAllDefaultsAsync(request.CustomerId, ct);
            var unsetDuration = (DateTime.UtcNow - unsetStart).TotalMilliseconds;
            
            // Set as default
            method.SetAsDefault();
            var saveStart = DateTime.UtcNow;
            await repo.SaveChangesAsync(ct);
            var saveDuration = (DateTime.UtcNow - saveStart).TotalMilliseconds;
            
            var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogInformation(
                "[SAVED_PAYMENT_METHODS] [SET_DEFAULT] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Success - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}, Type: {Type}, Last4: {Last4}, UnsetDuration: {UnsetDuration}ms, SaveDuration: {SaveDuration}ms, TotalDuration: {TotalDuration}ms",
                DateTime.UtcNow, request.Id, request.CustomerId, method.Type, method.Last4Digits, unsetDuration, saveDuration, totalDuration);
            
            return Result.Ok(SavedPaymentMethodMapper.ToDto(method));
        }
        catch (Exception ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logger.LogError(ex,
                "[SAVED_PAYMENT_METHODS] [SET_DEFAULT] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Error - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}, Duration: {Duration}ms, Exception: {Exception}, StackTrace: {StackTrace}",
                DateTime.UtcNow, request.Id, request.CustomerId, duration, ex.Message, ex.StackTrace);
            return Result.Fail<SavedPaymentMethodDto>($"Failed to set default payment method: {ex.Message}");
        }
    }
}

// Mapper
internal static class SavedPaymentMethodMapper
{
    public static SavedPaymentMethodDto ToDto(SavedPaymentMethod method) => new(
        method.Id,
        method.CustomerId,
        method.Type switch
        {
            PaymentMethodType.CreditCard => PaymentMethodTypeDto.CreditCard,
            PaymentMethodType.DebitCard => PaymentMethodTypeDto.DebitCard,
            PaymentMethodType.EWallet => PaymentMethodTypeDto.EWallet,
            _ => PaymentMethodTypeDto.CreditCard
        },
        method.Last4Digits,
        method.CardBrand,
        method.ExpiryMonth,
        method.ExpiryYear,
        method.CardholderName,
        method.IsDefault,
        method.IsActive,
        method.LastUsedAt,
        method.CreatedAt,
        method.UpdatedAt
    );
}
