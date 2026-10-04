using System;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Rating.Domain;

public class Rating : Entity
{
    public Guid BookingId { get; private set; }
    public Guid DriverId { get; private set; }
    public Guid CustomerId { get; private set; }
    public int Stars { get; private set; } // 1-5 stars
    public string? Comment { get; private set; }
    public RatingCategory? Category { get; private set; }
    public DateTime RatedAt { get; private set; }

    private Rating() { }

    public Rating(
        Guid bookingId,
        Guid driverId,
        Guid customerId,
        int stars,
        string? comment = null,
        RatingCategory? category = null)
    {
        if (stars < 1 || stars > 5)
            throw new ArgumentException("Rating must be between 1 and 5 stars", nameof(stars));

        BookingId = bookingId;
        DriverId = driverId;
        CustomerId = customerId;
        Stars = stars;
        Comment = comment;
        Category = category;
        RatedAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }

    public void UpdateComment(string? comment)
    {
        Comment = comment;
        UpdatedAt = DateTime.UtcNow;
    }
}

public enum RatingCategory
{
    Punctuality,    // Driver arrived on time
    Service,        // Quality of service
    Communication,  // Communication with customer
    Overall         // Overall experience
}
