using BeeLogistics.Modules.Bookings.Application.Handlers;
using FluentValidation;

namespace BeeLogistics.Modules.Bookings.Application.Validators;

public class CreateCustomerCommandValidator : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerCommandValidator()
    {
        RuleFor(x => x.Dto.Name)
            .NotEmpty().WithMessage("Name is required")
            .MaximumLength(255).WithMessage("Name must not exceed 255 characters");

        RuleFor(x => x.Dto.Email)
            .NotEmpty().WithMessage("Email is required")
            .EmailAddress().WithMessage("Invalid email format")
            .MaximumLength(255).WithMessage("Email must not exceed 255 characters");

        RuleFor(x => x.Dto.Phone)
            .MaximumLength(50).WithMessage("Phone must not exceed 50 characters");

        RuleFor(x => x.Dto.Address)
            .MaximumLength(500).WithMessage("Address must not exceed 500 characters");
    }
}

public class UpdateCustomerCommandValidator : AbstractValidator<UpdateCustomerCommand>
{
    public UpdateCustomerCommandValidator()
    {
        RuleFor(x => x.Dto.Name)
            .NotEmpty().WithMessage("Name is required")
            .MaximumLength(255).WithMessage("Name must not exceed 255 characters");

        RuleFor(x => x.Dto.Phone)
            .MaximumLength(50).WithMessage("Phone must not exceed 50 characters");

        RuleFor(x => x.Dto.Address)
            .MaximumLength(500).WithMessage("Address must not exceed 500 characters");
    }
}
