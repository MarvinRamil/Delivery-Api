using BeeLogistics.Modules.CRM.Domain;

namespace BeeLogistics.Modules.CRM.Application.DTOs;

// Customer Profile DTOs
public record CustomerProfileDto(
    Guid Id,
    string UserId,
    string Email,
    string FullName,
    string? Phone,
    string? CompanyName,
    string? Address,
    string Type,
    string Status,
    string? Notes,
    List<string> Tags,
    int TotalBookings,
    decimal TotalSpent,
    DateTime? LastBookingDate,
    DateTime? LastContactDate,
    DateTime CreatedAt
);

public record CreateCustomerProfileDto(
    string UserId,
    string Email,
    string FullName,
    string? Phone,
    string? CompanyName,
    CustomerType Type
);

public record UpdateCustomerProfileDto(
    string? Phone,
    string? CompanyName,
    string? Address,
    string? Notes,
    List<string>? Tags,
    CustomerStatus? Status
);

// Support Ticket DTOs
public record SupportTicketDto(
    Guid Id,
    string TicketNumber,
    Guid? CustomerProfileId,
    string? CustomerName,
    string? CustomerEmail,
    Guid? ConversationId,
    Guid? BookingId,
    string Subject,
    string? Description,
    string Category,
    string Priority,
    string Status,
    string? AssignedToUserId,
    string? AssignedToName,
    DateTime CreatedAt,
    DateTime? ResolvedAt,
    string? Resolution,
    int? ZammadTicketId = null,
    string UserType = "customer"
);

/// <summary>Zammad article (conversation message) from GET /api/v1/ticket_articles/by_ticket/{id}</summary>
public record ZammadArticleDto(string Body, string? Subject, string? From, string Sender, DateTime? CreatedAt, string ContentType, bool Internal);
/// <summary>Zammad ticket details fetched from Zammad API for display</summary>
public record ZammadDetailsDto(string Title, string State, List<ZammadArticleDto> Articles);
/// <summary>Support ticket with optional Zammad enrichment</summary>
public record SupportTicketWithZammadDto(SupportTicketDto Ticket, ZammadDetailsDto? Zammad);

public record CreateTicketDto(
    Guid? CustomerProfileId,
    string Subject,
    string? Description,
    TicketCategory Category,
    TicketPriority Priority,
    Guid? BookingId,
    string? UserType = "customer"
);

public record AddTicketCommentDto(string Body);

public record UpdateTicketDto(
    TicketStatus? Status,
    TicketPriority? Priority,
    string? AssignedToUserId,
    string? AssignedToName,
    string? Resolution
);

// Customer Note DTOs
public record CustomerNoteDto(
    Guid Id,
    string CreatedByName,
    string Content,
    bool IsPinned,
    DateTime CreatedAt
);

public record CreateNoteDto(
    Guid CustomerProfileId,
    string Content,
    bool IsPinned = false
);

// FAQ DTOs
public record FaqArticleDto(
    Guid Id,
    string Title,
    string Content,
    string Category,
    int SortOrder,
    bool IsPublished,
    int ViewCount,
    int HelpfulCount,
    int NotHelpfulCount,
    List<string> Tags
);

public record CreateFaqArticleDto(
    string Title,
    string Content,
    string Category,
    int SortOrder,
    bool IsPublished,
    List<string>? Tags
);

public record UpdateFaqArticleDto(
    string? Title,
    string? Content,
    string? Category,
    int? SortOrder,
    bool? IsPublished,
    List<string>? Tags
);

public record FaqCategoryDto(
    Guid Id,
    string Name,
    string? Description,
    string? Icon,
    int SortOrder,
    bool IsActive,
    int ArticleCount
);
