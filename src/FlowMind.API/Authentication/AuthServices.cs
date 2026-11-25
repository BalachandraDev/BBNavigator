using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Infrastructure.Database;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FlowMind.API.Authentication;

public static class Roles
{
    public const string PlatformAdmin = "PlatformAdmin";
    public const string TenantAdmin = "TenantAdmin";
    /// <summary>Ordinary company user: read approved knowledge and chat only.</summary>
    public const string TenantUser = "TenantUser";
}

public static class ClaimNames
{
    public const string Tenant = "tid";
    public const string Role = "role";
    /// <summary>Changes when a person's password changes or they are signed out everywhere; a token carries the value it was issued with.</summary>
    public const string Stamp = "stamp";
    public const string Email = "email";
    public const string Name = "name";
}

public sealed class AuthOptions
{
    public const string Section = "Auth";
    public string Issuer { get; set; } = "flowmind";
    public string Audience { get; set; } = "flowmind-api";
    /// <summary>HMAC signing key, at least 32 characters. Supply via environment/secret store, never source control.</summary>
    public string SigningKey { get; set; } = "";
    public int TokenMinutes { get; set; } = 60;
    /// <summary>Bootstrap secret for platform administrators (creates tenants). Empty = platform-admin login disabled.</summary>
    public string PlatformAdminKey { get; set; } = "";
}

public sealed class TokenService
{
    private readonly AuthOptions _opt;
    public TokenService(IOptions<AuthOptions> opt) => _opt = opt.Value;

    public (string Token, DateTime ExpiresUtc) Issue(string subject, string role, Guid? tenantId, string? stamp = null, string? email = null, string? name = null)
    {
        var issued = DateTime.UtcNow;
        var expires = issued.AddMinutes(_opt.TokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Iat, new DateTimeOffset(issued).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimNames.Role, role)
        };
        if (tenantId is { } t) claims.Add(new Claim(ClaimNames.Tenant, t.ToString()));
        if (stamp != null) claims.Add(new Claim(ClaimNames.Stamp, stamp));
        if (email != null) claims.Add(new Claim(ClaimNames.Email, email));
        if (name != null) claims.Add(new Claim(ClaimNames.Name, name));

        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.SigningKey)), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(_opt.Issuer, _opt.Audience, claims, expires: expires, signingCredentials: creds);
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
}

public static class ApiKeys
{
    /// <summary>256-bit random key. High entropy, so a plain SHA-256 is sufficient for storage.</summary>
    public static string Generate() => "fm_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    public static bool Verify(string key, string storedHash)
    {
        var a = Encoding.ASCII.GetBytes(Hash(key));
        var b = Encoding.ASCII.GetBytes(storedHash ?? "");
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}

/// <summary>Tenant comes ONLY from the validated JWT claim.</summary>
public sealed class HttpTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _http;
    public HttpTenantContext(IHttpContextAccessor http) => _http = http;

    /// <summary>Keys under which the server (never the client) scopes a public bot request after it has validated the link.</summary>
    public const string BotTenantItem = "flowmind.bot.tenant";
    public const string BotUserItem = "flowmind.bot.user";

    public Guid? TenantId =>
        Guid.TryParse(_http.HttpContext?.User.FindFirstValue(ClaimNames.Tenant), out var id) ? id
        : _http.HttpContext?.Items[BotTenantItem] is Guid bot ? bot : null;

    public string? UserId => _http.HttpContext?.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? _http.HttpContext?.Items[BotUserItem] as string;
}

public sealed class HttpAuditLogger : IAuditLogger
{
    private readonly FlowMindDbContext _db;
    private readonly IHttpContextAccessor _http;
    private readonly ITenantContext _tenant;

    public HttpAuditLogger(FlowMindDbContext db, IHttpContextAccessor http, ITenantContext tenant)
    {
        _db = db; _http = http; _tenant = tenant;
    }

    public async Task LogAsync(string action, string? entityType = null, string? entityId = null, string? detail = null,
        Guid? tenantId = null, CancellationToken ct = default)
    {
        var ctx = _http.HttpContext;
        _db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId ?? _tenant.TenantId,
            Actor = ctx?.User.FindFirstValue(ClaimNames.Email) ?? ctx?.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "anonymous",
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Detail = detail is { Length: > 1000 } ? detail[..1000] : detail,
            IpAddress = ctx?.Connection.RemoteIpAddress?.ToString()
        });
        await _db.SaveChangesAsync(ct);
    }
}
