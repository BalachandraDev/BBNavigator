using System.Security.Cryptography;
using System.Text;

namespace FlowMind.Infrastructure.Accounts;

/// <summary>
/// Password storage: PBKDF2 with HMAC-SHA256, a random 16-byte salt per password and a high iteration count, written as "v1$iterations$salt$hash".
/// The iteration count travels with the hash, so it can be raised later and old hashes still verify (and are upgraded at the next sign-in).
/// The plain password is never stored or logged.
/// </summary>
public static class PasswordHasher
{
    /// <summary>600,000 follows current OWASP guidance for PBKDF2-HMAC-SHA256. Tests lower it so they stay fast.</summary>
    public static int Iterations { get; set; } = 600_000;
    private const int SaltBytes = 16, HashBytes = 32;
    private static string? _dummy;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"v1${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>True when the password matches. <paramref name="needsRehash"/> is true when the stored hash uses fewer iterations than the current setting.</summary>
    public static bool Verify(string password, string? stored, out bool needsRehash)
    {
        needsRehash = false;
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations) || iterations < 1000 || iterations > 5_000_000) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            var ok = CryptographicOperations.FixedTimeEquals(actual, expected);
            needsRehash = ok && iterations < Iterations;
            return ok;
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Spends the same time as a real check, so "no such e-mail" cannot be told apart from "wrong password" by how long the answer takes.</summary>
    public static void BurnTime(string password)
    {
        _dummy ??= Hash("flowmind-timing-equaliser");
        Verify(password, _dummy, out _);
    }
}
