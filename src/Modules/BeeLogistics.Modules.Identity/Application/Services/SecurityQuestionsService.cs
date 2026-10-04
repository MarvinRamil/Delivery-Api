namespace BeeLogistics.Modules.Identity.Application.Services;

/// <summary>
/// Service for managing security questions
/// </summary>
public static class SecurityQuestionsService
{
    /// <summary>
    /// Common security questions that users can choose from
    /// </summary>
    public static readonly List<SecurityQuestion> CommonQuestions = new()
    {
        new SecurityQuestion(1, "What was the name of your first pet?"),
        new SecurityQuestion(2, "What city were you born in?"),
        new SecurityQuestion(3, "What was the name of your elementary school?"),
        new SecurityQuestion(4, "What was your mother's maiden name?"),
        new SecurityQuestion(5, "What was the make of your first car?"),
        new SecurityQuestion(6, "What was your childhood nickname?"),
        new SecurityQuestion(7, "What is the name of your favorite teacher?"),
        new SecurityQuestion(8, "What street did you grow up on?"),
        new SecurityQuestion(9, "What was your favorite food as a child?"),
        new SecurityQuestion(10, "What is the name of your best friend from childhood?"),
        new SecurityQuestion(11, "What was the name of your first employer?"),
        new SecurityQuestion(12, "What is your favorite movie?"),
        new SecurityQuestion(13, "What was the model of your first car?"),
        new SecurityQuestion(14, "What is your favorite book?"),
        new SecurityQuestion(15, "What is the name of the hospital where you were born?"),
        new SecurityQuestion(16, "What is your favorite sports team?"),
        new SecurityQuestion(17, "What was your favorite subject in school?"),
        new SecurityQuestion(18, "What is the name of your favorite restaurant?"),
        new SecurityQuestion(19, "What was the name of your first boss?"),
        new SecurityQuestion(20, "What is your favorite vacation destination?")
    };

    /// <summary>
    /// Get all available security questions
    /// </summary>
    public static List<SecurityQuestion> GetAllQuestions() => CommonQuestions;

    /// <summary>
    /// Get a security question by ID
    /// </summary>
    public static SecurityQuestion? GetQuestionById(int questionId)
    {
        return CommonQuestions.FirstOrDefault(q => q.Id == questionId);
    }

    /// <summary>
    /// Validate that a question ID exists
    /// </summary>
    public static bool IsValidQuestionId(int questionId)
    {
        return CommonQuestions.Any(q => q.Id == questionId);
    }
}

/// <summary>
/// Represents a security question
/// </summary>
public record SecurityQuestion(int Id, string Question);

