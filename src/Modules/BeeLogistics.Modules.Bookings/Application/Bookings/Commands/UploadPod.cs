using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Abstractions;
using MediatR;

// ICustomerIdentityResolver: resolves Bookings Customer.Id → Identity UserId for SignalR routing

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

/// <summary>
/// Command to upload proof of delivery.
/// Follows Single Responsibility Principle (SRP).
/// </summary>
public record UploadPodCommand(
    Guid BookingId,
    Guid StopId,
    Guid DriverId,
    string? ImagePath = null,
    string? SignaturePath = null,
    string? RecipientName = null,
    string? Notes = null
) : IRequest<Result<ProofOfDeliveryDto>>;

/// <summary>
/// Handler for uploading POD.
/// Follows Dependency Inversion Principle (DIP).
/// </summary>
public class UploadPodCommandHandler : IRequestHandler<UploadPodCommand, Result<ProofOfDeliveryDto>>
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IFileStorageService _fileStorage;

    public UploadPodCommandHandler(IBookingRepository bookingRepository, IFileStorageService fileStorage)
    {
        _bookingRepository = bookingRepository;
        _fileStorage = fileStorage;
    }

    public async Task<Result<ProofOfDeliveryDto>> Handle(UploadPodCommand request, CancellationToken ct)
    {
        var booking = await _bookingRepository.GetByIdAsync(request.BookingId, ct);
        if (booking == null)
            return Result.NotFound<ProofOfDeliveryDto>("Booking not found");

        if (booking.SelectedDriverId != request.DriverId)
            return Result.Forbidden<ProofOfDeliveryDto>("You do not have access to this booking");

        var stop = booking.Stops.FirstOrDefault(s => s.Id == request.StopId);
        if (stop == null)
            return Result.Fail<ProofOfDeliveryDto>("Stop not found");

        if (stop.Type != StopType.Dropoff)
            return Result.Fail<ProofOfDeliveryDto>("POD can only be uploaded for dropoff stops");

        var pod = new ProofOfDelivery(
            request.BookingId,
            request.StopId,
            request.ImagePath,
            DateTime.UtcNow,
            request.SignaturePath,
            request.RecipientName,
            request.Notes
        );

        booking.AddProofOfDelivery(pod);
        await _bookingRepository.SaveChangesAsync(ct);

        var dto = new ProofOfDeliveryDto(
            pod.Id,
            pod.BookingId,
            pod.StopId,
            pod.ImagePath,
            pod.SignaturePath,
            pod.DeliveredAt,
            pod.RecipientName,
            pod.Notes
        );

        var resolved = await BookingImageUrlResolver.ResolvePodAsync(dto, _fileStorage, ct);
        return Result.Ok(resolved);
    }
}
