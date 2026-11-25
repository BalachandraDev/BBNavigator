using System.Security.Claims;
using System.Text.Encodings.Web;
using FlowMind.Infrastructure.Database;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlowMind.API.Authentication;

/// <summary>
/// Lets an integration call the simple endpoints with just <c>X-Api-Key: &lt;key&gt;</c> (no token exchange).
/// The key identifies the company and its role: a user access key gives TenantUser, the company admin key gives TenantAdmin.
/// Revoked keys and deactivated companies are refused on every request. No header = not this scheme's business.
/// </summary>
public sealed class ApiKeyAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string Header = "X-Api-Key";

    public ApiKeyAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var values)) return AuthenticateResult.NoResult();
        var key = values.ToString().Trim();
        if (key.Length < 10 || key.Length > 200) return AuthenticateResult.Fail("Invalid API key.");

        // Resolved before any user exists, so this context is unscoped; the lookups below filter explicitly.
        var db = Context.RequestServices.GetRequiredService<FlowMindDbContext>();
        var hash = ApiKeys.Hash(key);

        var user = await db.AccessKeys.Where(k => k.KeyHash == hash && k.IsActive)
            .Select(k => new { k.Id, k.TenantId }).FirstOrDefaultAsync(Context.RequestAborted);
        if (user != null && await db.Tenants.AnyAsync(t => t.Id == user.TenantId && t.IsActive, Context.RequestAborted))
            return Success($"key:{user.Id:N}", Roles.TenantUser, user.TenantId);

        var tenant = await db.Tenants.Where(t => t.ApiKeyHash == hash && t.IsActive)
            .Select(t => new { t.Id, t.Slug }).FirstOrDefaultAsync(Context.RequestAborted);
        if (tenant != null) return Success($"tenant:{tenant.Slug}", Roles.TenantAdmin, tenant.Id);

        return AuthenticateResult.Fail("Invalid API key.");
    }

    private AuthenticateResult Success(string subject, string role, Guid tenantId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", subject), new Claim(ClaimNames.Role, role), new Claim(ClaimNames.Tenant, tenantId.ToString()),
        }, SchemeName, nameType: "sub", roleType: ClaimNames.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
