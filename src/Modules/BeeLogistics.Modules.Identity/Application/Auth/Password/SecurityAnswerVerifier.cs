using BeeLogistics.Modules.Identity.Domain;

namespace BeeLogistics.Modules.Identity.Application.Auth.Password;

/// <summary>One submitted answer to a numbered security question.</summary>
public sealed record SecurityAnswerInput(int? QuestionNumber, string? Answer);

/// <summary>
/// Checks submitted security answers against the hashes stored on the user.
/// <para>
/// Extracted verbatim from the identical loops that lived in both <c>ForgotPassword</c> and
/// <c>ResetPassword</c>. Semantics preserved exactly: answers with no question number or a blank
/// answer are skipped, and <b>any one</b> correct answer is sufficient.
/// </para>
/// </summary>
internal static class SecurityAnswerVerifier
{
    public static bool HasSecurityQuestions(ApplicationUser user)
        => user.SecurityQuestionId1.HasValue
           || user.SecurityQuestionId2.HasValue
           || user.SecurityQuestionId3.HasValue;

    public static bool AnyAnswerCorrect(ApplicationUser user, IReadOnlyList<SecurityAnswerInput>? answers)
    {
        if (answers == null) return false;

        foreach (var answer in answers)
        {
            if (!answer.QuestionNumber.HasValue || string.IsNullOrWhiteSpace(answer.Answer))
                continue;

            var questionNumber = answer.QuestionNumber.Value;
            var providedAnswerHash = AuthHelpers.HashSecurityAnswer(answer.Answer);

            if (questionNumber == 1 && user.SecurityQuestionId1.HasValue &&
                providedAnswerHash == user.SecurityAnswerHash1)
            {
                return true;
            }

            if (questionNumber == 2 && user.SecurityQuestionId2.HasValue &&
                providedAnswerHash == user.SecurityAnswerHash2)
            {
                return true;
            }

            if (questionNumber == 3 && user.SecurityQuestionId3.HasValue &&
                providedAnswerHash == user.SecurityAnswerHash3)
            {
                return true;
            }
        }

        return false;
    }
}
