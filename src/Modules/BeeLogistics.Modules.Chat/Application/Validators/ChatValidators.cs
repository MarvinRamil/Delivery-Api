using BeeLogistics.Modules.Chat.Application.Handlers;
using FluentValidation;

namespace BeeLogistics.Modules.Chat.Application.Validators;

public class SendMessageCommandValidator : AbstractValidator<SendMessageCommand>
{
    public SendMessageCommandValidator()
    {
        RuleFor(x => x.Data.ConversationId)
            .NotEmpty().WithMessage("Conversation ID is required")
            .NotEqual(Guid.Empty).WithMessage("Conversation ID cannot be empty");

        RuleFor(x => x.Data.Content)
            .NotEmpty().WithMessage("Message content is required")
            .MaximumLength(5000).WithMessage("Message content must not exceed 5000 characters");

        RuleFor(x => x.SenderId)
            .NotEmpty().WithMessage("Sender ID is required");

        RuleFor(x => x.SenderName)
            .NotEmpty().WithMessage("Sender name is required")
            .MaximumLength(100).WithMessage("Sender name must not exceed 100 characters");
    }
}

public class CreateConversationCommandValidator : AbstractValidator<CreateConversationCommand>
{
    public CreateConversationCommandValidator()
    {
        RuleFor(x => x.Data.Title)
            .NotEmpty().WithMessage("Conversation title is required")
            .MaximumLength(200).WithMessage("Conversation title must not exceed 200 characters");

        RuleFor(x => x.Data.Type)
            .IsInEnum().WithMessage("Invalid conversation type");

        RuleFor(x => x.CreatorId)
            .NotEmpty().WithMessage("Creator ID is required");

        RuleFor(x => x.CreatorName)
            .NotEmpty().WithMessage("Creator name is required")
            .MaximumLength(100).WithMessage("Creator name must not exceed 100 characters");
    }
}
