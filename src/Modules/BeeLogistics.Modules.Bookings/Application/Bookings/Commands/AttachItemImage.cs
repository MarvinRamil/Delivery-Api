using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

/// <summary>
/// Records an already-uploaded item image against a booking and returns the viewable URL.
/// </summary>
/// <remarks>
/// The file itself is processed and stored by the upload endpoint - by the time this runs the
/// bytes are already in object storage and only the path needs persisting. Split that way
/// because storing the file needs <c>IFormFile</c>, which belongs to the HTTP layer.
///
/// Returns <see cref="ResultErrorKind.NotFound"/> when the booking has disappeared between the
/// create and this call. Every caller treats that as "keep the response you already have"
/// rather than an error, so it must stay distinguishable from a failed write.
/// </remarks>
public record AttachItemImageCommand(Guid BookingId, string ItemImagePath) : IRequest<Result<string?>>;

public class AttachItemImageCommandHandler : IRequestHandler<AttachItemImageCommand, Result<string?>>
{
    private readonly IBookingRepository _repository;
    private readonly IFileStorageService _fileStorage;

    public AttachItemImageCommandHandler(IBookingRepository repository, IFileStorageService fileStorage)
    {
        _repository = repository;
        _fileStorage = fileStorage;
    }

    public async Task<Result<string?>> Handle(AttachItemImageCommand request, CancellationToken ct)
    {
        var booking = await _repository.GetByIdAsync(request.BookingId, ct);
        if (booking == null)
            return Result.NotFound<string?>("Booking not found");

        booking.SetItemImagePath(request.ItemImagePath);
        await _repository.SaveChangesAsync(ct);

        var viewableImageUrl = await BookingImageUrlResolver.ResolveStoragePathAsync(request.ItemImagePath, _fileStorage, ct: ct);
        return Result.Ok(viewableImageUrl);
    }
}
