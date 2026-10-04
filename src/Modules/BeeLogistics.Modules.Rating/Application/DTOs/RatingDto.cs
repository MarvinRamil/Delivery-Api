using BeeLogistics.Modules.Rating.Domain;

namespace BeeLogistics.Modules.Rating.Application.DTOs;

public record RatingDto(
    Guid Id,
    Guid BookingId,
    Guid DriverId,
    Guid CustomerId,
    int Stars,
    string? Comment,
    RatingCategory? Category,
    DateTime RatedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record DriverRatingDto(
    Guid Id,
    Guid DriverId,
    decimal AverageRating,
    int TotalRatings,
    int FiveStarCount,
    int FourStarCount,
    int ThreeStarCount,
    int TwoStarCount,
    int OneStarCount,
    DateTime LastUpdatedAt
);

public record CreateRatingDto(
    Guid BookingId,
    Guid DriverId,
    int Stars,
    string? Comment,
    RatingCategory? Category
);

public record UpdateRatingDto(
    string? Comment
);
