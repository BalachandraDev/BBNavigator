namespace FlowMind.Infrastructure.Accounts;

/// <summary>
/// What makes an acceptable password. Length matters most, so there are no forced symbols: at least 10 characters, not one of the passwords
/// everybody tries, not made from the person's e-mail or name, and not one repeated character. The reasons are returned in plain words.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 10, MaxLength = 128;

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password12", "password123", "password1234", "passw0rd123", "1234567890", "12345678910", "0123456789", "1111111111",
        "qwertyuiop", "qwerty12345", "qwerty123456", "abcdefghij", "abcd123456", "iloveyou123", "letmein1234", "welcome1234", "welcome123", "admin12345",
        "administrator", "changeme123", "changeme1234", "flowmind123", "flowmind1234", "p@ssw0rd123", "p@ssword123", "123456789a", "a123456789", "1q2w3e4r5t",
        "hospital123", "hospital1234", "test123456", "default1234",
    };

    /// <returns>null when the password is acceptable, otherwise what to fix.</returns>
    public static string? Check(string? password, string? email = null, string? displayName = null)
    {
        var p = password ?? "";
        if (p.Length < MinLength) return $"Use at least {MinLength} characters.";
        if (p.Length > MaxLength) return $"Use at most {MaxLength} characters.";
        if (p.Distinct().Count() < 4) return "That password repeats too few different characters. Mix it up.";
        if (Common.Contains(p)) return "That password is too common. Choose something less guessable, for example a few unrelated words.";
        if (!string.IsNullOrWhiteSpace(email))
        {
            var e = email.Trim();
            var local = e.Split('@')[0];
            if (p.Equals(e, StringComparison.OrdinalIgnoreCase) || (local.Length >= 4 && p.Contains(local, StringComparison.OrdinalIgnoreCase)))
                return "The password must not contain your e-mail address.";
        }
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            foreach (var part in displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (part.Length >= 4 && p.Contains(part, StringComparison.OrdinalIgnoreCase)) return "The password must not contain your name.";
        }
        return null;
    }
}
