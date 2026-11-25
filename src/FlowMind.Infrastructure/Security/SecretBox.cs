using System.Security.Cryptography;
using System.Text;

namespace FlowMind.Infrastructure.Security;

/// <summary>
/// Authenticated encryption (AES-256-GCM) for the one secret the platform admin may need to read back: a company's key.
/// The key is derived from the server secret <c>Security:SecretsKey</c>, which lives outside the database (environment
/// variable / secret store). Anyone who steals only the database cannot read the stored keys; tampering is detected.
/// Without a configured secret the box is disabled and nothing recoverable is ever stored.
/// </summary>
public sealed class SecretBox
{
    private const string Prefix = "v1.";
    private readonly byte[]? _key;

    public SecretBox(string? secretsKey)
    {
        if (!string.IsNullOrWhiteSpace(secretsKey) && secretsKey.Length >= 32)
            _key = SHA256.HashData(Encoding.UTF8.GetBytes("flowmind.secretbox.v1|" + secretsKey));
    }

    public bool Enabled => _key != null;

    public string Encrypt(string plain)
    {
        if (_key == null) throw new InvalidOperationException("Security:SecretsKey is not configured.");
        var data = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[data.Length];
        using (var aes = new AesGcm(_key, 16)) aes.Encrypt(nonce, data, cipher, tag);
        return Prefix + Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    /// <summary>Returns null when disabled, malformed, tampered with, or encrypted under a different server secret.</summary>
    public string? TryDecrypt(string? token)
    {
        if (_key == null || string.IsNullOrEmpty(token) || !token.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var raw = Convert.FromBase64String(token[Prefix.Length..]);
            if (raw.Length < 28) return null;
            var nonce = raw[..12]; var tag = raw[12..28]; var cipher = raw[28..];
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(_key, 16)) aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }
}
