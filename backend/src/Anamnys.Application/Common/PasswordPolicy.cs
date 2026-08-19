using System.Text.RegularExpressions;

namespace Anamnys.Application.Common;

/// <summary>
/// Server-side mirror of the client's password-strength checklist (see the mobile app's
/// src/utils/password.ts) — the UI must never accept a password the server would reject.
/// Minimum 10 characters, at least one uppercase, one lowercase, one digit, one symbol.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;

    private static readonly Regex Lower = new("[a-z]", RegexOptions.Compiled);
    private static readonly Regex Upper = new("[A-Z]", RegexOptions.Compiled);
    private static readonly Regex Digit = new("[0-9]", RegexOptions.Compiled);
    private static readonly Regex Symbol = new(@"[^a-zA-Z0-9]", RegexOptions.Compiled);

    /// <summary>Returns null if the password satisfies the policy, otherwise a user-facing message.</summary>
    public static string? Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return $"Password must be at least {MinLength} characters and include uppercase, lowercase, a number, and a symbol.";

        if (!Lower.IsMatch(password) || !Upper.IsMatch(password) ||
            !Digit.IsMatch(password) || !Symbol.IsMatch(password))
            return $"Password must be at least {MinLength} characters and include uppercase, lowercase, a number, and a symbol.";

        return null;
    }
}
