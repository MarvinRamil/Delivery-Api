using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Handlers;
using FluentValidation;

namespace BeeLogistics.Modules.Bookings.Application.Validators;

/// <summary>
/// Field-shape validation for booking creation.
/// </summary>
/// <remarks>
/// Deliberately does not check stop counts, pickup/dropoff composition, or service type. Those
/// stay in <see cref="CreateBookingCommandHandler"/>: this validator throws, and
/// ExceptionHandlingMiddleware renders that as <c>{ success, errors: [...] }</c>, whereas the
/// handler's Result.Fail renders <c>{ success, message }</c>. Moving the existing checks here
/// would change the error shape clients parse for no benefit.
/// </remarks>
public class CreateBookingCommandValidator : AbstractValidator<CreateBookingCommand>
{
    public CreateBookingCommandValidator()
    {
        // CustomerId is only required if UserEmail is not provided
        // When UserEmail is provided (from JWT token), the handler will resolve/create customer by email
        RuleFor(x => x.Dto.CustomerId)
            .NotEmpty().WithMessage("Customer ID is required")
            .When(x => string.IsNullOrEmpty(x.UserEmail));

        RuleFor(x => x.Dto.VehicleType)
            .NotEmpty().WithMessage("Vehicle type is required")
            .MaximumLength(50).WithMessage("Vehicle type must not exceed 50 characters");

        RuleFor(x => x.Dto.CargoDescription)
            .MaximumLength(1000).WithMessage("Cargo description must not exceed 1000 characters");

        RuleFor(x => x.Dto.Notes)
            .MaximumLength(1000).WithMessage("Notes must not exceed 1000 characters");

        RuleFor(x => x.Dto.ScheduleDate)
            .Must(date =>
            {
                // Normalize to UTC for comparison
                var dateUtc = date.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
                    : date.ToUniversalTime();
                return dateUtc >= DateTime.UtcNow.Date;
            })
            .WithMessage("Schedule date must be today or in the future");

        RuleFor(x => x.Dto.EstimatedFare)
            .GreaterThanOrEqualTo(0).WithMessage("Estimated fare must be greater than or equal to 0");

        RuleFor(x => x.Dto.WeightKg)
            .GreaterThanOrEqualTo(0).WithMessage("Weight must be greater than or equal to 0")
            .LessThanOrEqualTo(100000).WithMessage("Weight must not exceed 100,000 kg")
            .When(x => x.Dto.WeightKg.HasValue);

        RuleForEach(x => x.Dto.Stops)
            .SetValidator(new DeliveryStopDtoValidator())
            .When(x => x.Dto.Stops != null);
    }
}

/// <summary>
/// Per-stop field validation. Coordinates are optional at this layer but must be a valid,
/// complete pair when present — a lone latitude silently geocodes to nowhere and yields a fare
/// computed from a fallback estimate rather than the real route.
/// </summary>
public class DeliveryStopDtoValidator : AbstractValidator<DeliveryStopDto>
{
    public DeliveryStopDtoValidator()
    {
        RuleFor(s => s.Address)
            .NotEmpty().WithMessage("Stop address is required")
            .MaximumLength(500).WithMessage("Stop address must not exceed 500 characters");

        RuleFor(s => s.Type)
            .Must(t => t != null &&
                       (t.Equals("Pickup", StringComparison.OrdinalIgnoreCase) ||
                        t.Equals("Dropoff", StringComparison.OrdinalIgnoreCase)))
            .WithMessage("Stop type must be either 'Pickup' or 'Dropoff'");

        RuleFor(s => s.Latitude)
            .InclusiveBetween(-90, 90).WithMessage("Stop latitude must be between -90 and 90")
            .When(s => s.Latitude.HasValue);

        RuleFor(s => s.Longitude)
            .InclusiveBetween(-180, 180).WithMessage("Stop longitude must be between -180 and 180")
            .When(s => s.Longitude.HasValue);

        RuleFor(s => s)
            .Must(s => s.Latitude.HasValue == s.Longitude.HasValue)
            .WithMessage("Both stop latitude and longitude must be provided together");

        RuleFor(s => s.ContactPhone)
            .MaximumLength(30).WithMessage("Contact phone must not exceed 30 characters");

        RuleFor(s => s.ContactName)
            .MaximumLength(100).WithMessage("Contact name must not exceed 100 characters");

        RuleFor(s => s.Notes)
            .MaximumLength(500).WithMessage("Stop notes must not exceed 500 characters");
    }
}
