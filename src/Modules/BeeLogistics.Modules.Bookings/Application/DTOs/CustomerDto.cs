namespace BeeLogistics.Modules.Bookings.Application.DTOs;

public record CustomerDto(
    Guid Id,
    string Name,
    string Email,
    string CompanyName,
    string Phone,
    string Address,
    bool IsActive,
    bool IsEmailVerified,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record CreateCustomerDto(
    string Name,
    string Email,
    string CompanyName,
    string Phone,
    string Address,
    string? Password
);

public record UpdateCustomerDto(
    string Name,
    string CompanyName,
    string Phone,
    string Address
);
