using BeeLogistics.Modules.Rating.Application.DTOs;
using BeeLogistics.Modules.Rating.Application.Interfaces;
using Domain = BeeLogistics.Modules.Rating.Domain;
using BeeLogistics.Shared.Abstractions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Rating.Application.Handlers;

// Queries
public record GetRatingByIdQuery(Guid Id) : IRequest<Result<RatingDto>>;
public record GetRatingByBookingIdQuery(Guid BookingId) : IRequest<Result<RatingDto?>>;
public record GetRatingsByDriverIdQuery(Guid DriverId) : IRequest<Result<IReadOnlyList<RatingDto>>>;
public record GetRatingsByCustomerIdQuery(Guid CustomerId) : IRequest<Result<IReadOnlyList<RatingDto>>>;
public record GetDriverRatingQuery(Guid DriverId) : IRequest<Result<DriverRatingDto>>;

// Commands
public record CreateRatingCommand(
    Guid BookingId,
    Guid DriverId,
    Guid CustomerId,
    int Stars,
    string? Comment,
    Domain.RatingCategory? Category
) : IRequest<Result<RatingDto>>;

public record UpdateRatingCommand(Guid Id, string? Comment) : IRequest<Result<RatingDto>>;

// Query Handlers
public class GetRatingByIdQueryHandler : IRequestHandler<GetRatingByIdQuery, Result<RatingDto>>
{
    private readonly IRatingRepository _repository;

    public GetRatingByIdQueryHandler(IRatingRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<RatingDto>> Handle(GetRatingByIdQuery request, CancellationToken ct)
    {
        var rating = await _repository.GetByIdAsync(request.Id, ct);
        if (rating == null)
            return Result.NotFound<RatingDto>("Rating not found");

        var dto = new RatingDto(
            rating.Id,
            rating.BookingId,
            rating.DriverId,
            rating.CustomerId,
            rating.Stars,
            rating.Comment,
            rating.Category,
            rating.RatedAt,
            rating.CreatedAt,
            rating.UpdatedAt
        );

        return Result.Ok(dto);
    }
}

public class GetRatingByBookingIdQueryHandler : IRequestHandler<GetRatingByBookingIdQuery, Result<RatingDto?>>
{
    private readonly IRatingRepository _repository;

    public GetRatingByBookingIdQueryHandler(IRatingRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<RatingDto?>> Handle(GetRatingByBookingIdQuery request, CancellationToken ct)
    {
        var rating = await _repository.GetByBookingIdAsync(request.BookingId, ct);
        if (rating == null)
            return Result.Ok<RatingDto?>(null);

        var dto = new RatingDto(
            rating.Id,
            rating.BookingId,
            rating.DriverId,
            rating.CustomerId,
            rating.Stars,
            rating.Comment,
            rating.Category,
            rating.RatedAt,
            rating.CreatedAt,
            rating.UpdatedAt
        );

        return Result.Ok<RatingDto?>(dto);
    }
}

public class GetRatingsByDriverIdQueryHandler : IRequestHandler<GetRatingsByDriverIdQuery, Result<IReadOnlyList<RatingDto>>>
{
    private readonly IRatingRepository _repository;

    public GetRatingsByDriverIdQueryHandler(IRatingRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<RatingDto>>> Handle(GetRatingsByDriverIdQuery request, CancellationToken ct)
    {
        var ratings = await _repository.GetByDriverIdAsync(request.DriverId, ct);

        var dtos = ratings.Select(r => new RatingDto(
            r.Id,
            r.BookingId,
            r.DriverId,
            r.CustomerId,
            r.Stars,
            r.Comment,
            r.Category,
            r.RatedAt,
            r.CreatedAt,
            r.UpdatedAt
        )).ToList();

        return Result.Ok<IReadOnlyList<RatingDto>>(dtos);
    }
}

public class GetRatingsByCustomerIdQueryHandler : IRequestHandler<GetRatingsByCustomerIdQuery, Result<IReadOnlyList<RatingDto>>>
{
    private readonly IRatingRepository _repository;

    public GetRatingsByCustomerIdQueryHandler(IRatingRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<RatingDto>>> Handle(GetRatingsByCustomerIdQuery request, CancellationToken ct)
    {
        var ratings = await _repository.GetByCustomerIdAsync(request.CustomerId, ct);

        var dtos = ratings.Select(r => new RatingDto(
            r.Id,
            r.BookingId,
            r.DriverId,
            r.CustomerId,
            r.Stars,
            r.Comment,
            r.Category,
            r.RatedAt,
            r.CreatedAt,
            r.UpdatedAt
        )).ToList();

        return Result.Ok<IReadOnlyList<RatingDto>>(dtos);
    }
}

public class GetDriverRatingQueryHandler : IRequestHandler<GetDriverRatingQuery, Result<DriverRatingDto>>
{
    private readonly IDriverRatingRepository _repository;

    public GetDriverRatingQueryHandler(IDriverRatingRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<DriverRatingDto>> Handle(GetDriverRatingQuery request, CancellationToken ct)
    {
        var driverRating = await _repository.GetOrCreateByDriverIdAsync(request.DriverId, ct);

        var dto = new DriverRatingDto(
            driverRating.Id,
            driverRating.DriverId,
            driverRating.AverageRating,
            driverRating.TotalRatings,
            driverRating.FiveStarCount,
            driverRating.FourStarCount,
            driverRating.ThreeStarCount,
            driverRating.TwoStarCount,
            driverRating.OneStarCount,
            driverRating.LastUpdatedAt
        );

        return Result.Ok(dto);
    }
}

// Command Handlers
public class CreateRatingCommandHandler : IRequestHandler<CreateRatingCommand, Result<RatingDto>>
{
    private readonly IRatingRepository _ratingRepository;
    private readonly IDriverRatingRepository _driverRatingRepository;
    private readonly ILogger<CreateRatingCommandHandler> _logger;

    public CreateRatingCommandHandler(
        IRatingRepository ratingRepository,
        IDriverRatingRepository driverRatingRepository,
        ILogger<CreateRatingCommandHandler> logger)
    {
        _ratingRepository = ratingRepository;
        _driverRatingRepository = driverRatingRepository;
        _logger = logger;
    }

    public async Task<Result<RatingDto>> Handle(CreateRatingCommand request, CancellationToken ct)
    {
        // Check if rating already exists for this booking
        var existing = await _ratingRepository.GetByBookingIdAsync(request.BookingId, ct);
        if (existing != null)
        {
            return Result.Fail<RatingDto>("Rating already exists for this booking");
        }

        try
        {
            // Create rating
            var rating = new Domain.Rating(
                request.BookingId,
                request.DriverId,
                request.CustomerId,
                request.Stars,
                request.Comment,
                request.Category
            );

            _ratingRepository.Add(rating);
            await _ratingRepository.SaveChangesAsync(ct);

            // Update driver rating aggregation
            var driverRating = await _driverRatingRepository.GetOrCreateByDriverIdAsync(request.DriverId, ct);
            driverRating.AddRating(request.Stars);
            _driverRatingRepository.Update(driverRating);
            await _driverRatingRepository.SaveChangesAsync(ct);

            var dto = new RatingDto(
                rating.Id,
                rating.BookingId,
                rating.DriverId,
                rating.CustomerId,
                rating.Stars,
                rating.Comment,
                rating.Category,
                rating.RatedAt,
                rating.CreatedAt,
                rating.UpdatedAt
            );

            return Result.Ok(dto);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid rating data: {Message}", ex.Message);
            return Result.Fail<RatingDto>(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating rating for booking {BookingId}", request.BookingId);
            return Result.Fail<RatingDto>("An error occurred while creating the rating. Please try again.");
        }
    }
}

public class UpdateRatingCommandHandler : IRequestHandler<UpdateRatingCommand, Result<RatingDto>>
{
    private readonly IRatingRepository _repository;
    private readonly ILogger<UpdateRatingCommandHandler> _logger;

    public UpdateRatingCommandHandler(IRatingRepository repository, ILogger<UpdateRatingCommandHandler> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<Result<RatingDto>> Handle(UpdateRatingCommand request, CancellationToken ct)
    {
        var rating = await _repository.GetByIdAsync(request.Id, ct);
        if (rating == null)
            return Result.NotFound<RatingDto>("Rating not found");

        try
        {
            rating.UpdateComment(request.Comment);
            await _repository.SaveChangesAsync(ct);

            var dto = new RatingDto(
                rating.Id,
                rating.BookingId,
                rating.DriverId,
                rating.CustomerId,
                rating.Stars,
                rating.Comment,
                rating.Category,
                rating.RatedAt,
                rating.CreatedAt,
                rating.UpdatedAt
            );

            return Result.Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating rating {RatingId}", request.Id);
            return Result.Fail<RatingDto>("An error occurred while updating the rating. Please try again.");
        }
    }
}
