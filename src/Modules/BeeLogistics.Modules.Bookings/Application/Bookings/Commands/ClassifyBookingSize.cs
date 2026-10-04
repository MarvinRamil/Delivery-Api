using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

public record ClassifyBookingSizeCommand(Guid BookingId, BookingSize Size, Guid UserId) : IRequest<Result<BookingDto>>;

public class ClassifyBookingSizeCommandHandler : IRequestHandler<ClassifyBookingSizeCommand, Result<BookingDto>>
{
    private readonly IBookingRepository _repository;
    private readonly IFileStorageService _fileStorage;

    public ClassifyBookingSizeCommandHandler(IBookingRepository repository, IFileStorageService fileStorage)
    {
        _repository = repository;
        _fileStorage = fileStorage;
    }

    public async Task<Result<BookingDto>> Handle(ClassifyBookingSizeCommand request, CancellationToken ct)
    {
        // Authorization is handled by controller's [Authorize(Roles = "Admin,Owner")]
        // Back-office app is only for BEE admin, so no need for additional checks
        
        var booking = await _repository.GetByIdAsync(request.BookingId, ct);
        if (booking == null)
            return Result.NotFound<BookingDto>("Booking not found");

        // Only pending assignment bookings can be classified
        if (booking.AssignmentStatus != BookingAssignmentStatus.PendingAssignment)
            return Result.Fail<BookingDto>("Booking has already been assigned or classified");

        booking.ClassifySize(request.Size);

        // Removed automatic broadcasting on Small classification
        // Admin must manually trigger broadcast via start-broadcast endpoint

        await _repository.SaveChangesAsync(ct);
        var dto = await BookingImageUrlResolver.ResolveAsync(BookingMapper.ToDto(booking), _fileStorage, ct);
        return Result.Ok(dto);
    }
}
