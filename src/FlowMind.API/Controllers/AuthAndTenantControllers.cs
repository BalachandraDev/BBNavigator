using FlowMind.API.Authentication;
using FlowMind.Application.DTOs;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Infrastructure.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlowMind.API.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly FlowMindDbContext _db;
    private readonly TokenService _tokens;
    private readonly AuthOptions _auth;
    private readonly IAuditLogger _audit;

    public AuthController(FlowMindDbContext db, TokenService tokens, IOptions<AuthOptions> auth, IAuditLogger audit)
    {
        _db = db; _tokens = tokens; _auth = auth.Value; _audit = audit;
    }

    /// <summary>Exchange a tenant API key (or the platform admin key) for a short-lived JWT.</summary>
    [HttpPost("token")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<TokenResponse>> Token(TokenRequest req, CancellationToken ct)
    {
        (string Token, DateTime ExpiresUtc)? issued = null;

        if (string.IsNullOrWhiteSpace(req.TenantSlug))
        {
            if (_auth.PlatformAdminKey.Length >= 16 && ApiKeys.FixedTimeEquals(req.ApiKey, _auth.PlatformAdminKey))
                issued = _tokens.Issue("platform-admin", Roles.PlatformAdmin, null);
        }
        else
        {
            var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == req.TenantSlug && t.IsActive, ct);
            if (tenant != null && ApiKeys.Verify(req.ApiKey, tenant.ApiKeyHash))
            {
                issued = _tokens.Issue($"tenant:{tenant.Slug}", Roles.TenantAdmin, tenant.Id);
                await _audit.LogAsync("auth.token", "Tenant", tenant.Id.ToString(), tenantId: tenant.Id, ct: ct);
            }
            else if (tenant != null)
            {
                // Not the company admin key: it may be a user access key (chat + read-only knowledge).
                var hash = ApiKeys.Hash(req.ApiKey);
                var key = await _db.AccessKeys.FirstOrDefaultAsync(k => k.TenantId == tenant.Id && k.KeyHash == hash && k.IsActive, ct);
                if (key != null)
                {
                    issued = _tokens.Issue($"key:{key.Id:N}", Roles.TenantUser, tenant.Id);
                    key.LastUsedUtc = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    await _audit.LogAsync("auth.token", "AccessKey", key.Id.ToString(), key.Name, tenant.Id, ct);
                }
            }
        }

        // One generic failure for unknown tenant / wrong key / disabled platform login: no enumeration.
        if (issued is null)
        {
            // Best effort: an audit-store outage must not change the response to the caller.
            try { await _audit.LogAsync("auth.failed", detail: $"slug={req.TenantSlug}", ct: ct); }
            catch (Exception ex) { HttpContext.RequestServices.GetRequiredService<ILogger<AuthController>>().LogError(ex, "Could not write audit log"); }
            return Unauthorized(new ProblemDetails { Status = 401, Title = "Invalid credentials." });
        }
        return new TokenResponse(issued.Value.Token, "Bearer", issued.Value.ExpiresUtc);
    }
}


[ApiController]
[Route("api/v1/applications")]
[Authorize(Policy = "TenantMember")]
public class ApplicationsController : ControllerBase
{
    private readonly FlowMindDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IAuditLogger _audit;
    public ApplicationsController(FlowMindDbContext db, ITenantContext tenant, IAuditLogger audit)
    { _db = db; _tenant = tenant; _audit = audit; }

    [HttpPost]
    [Authorize(Policy = "TenantAdmin")]
    public async Task<ActionResult<ApplicationResponse>> Create(CreateApplicationRequest req, CancellationToken ct)
    {
        var tenantId = _tenant.TenantId!.Value;
        var name = req.Name.Trim();
        if (await _db.Applications.AnyAsync(a => a.Name == name, ct)) // filtered to this tenant
            return Conflict(new ProblemDetails { Status = 409, Title = "An application with this name already exists." });

        var app = new TenantApplication { TenantId = tenantId, Name = name, Description = req.Description?.Trim() };
        _db.Applications.Add(app);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("application.create", "Application", app.Id.ToString(), app.Name, ct: ct);
        return Created($"/api/v1/applications/{app.Id}", new ApplicationResponse(app.Id, app.Name, app.Description, app.CreatedUtc));
    }

    [HttpGet]
    public async Task<IReadOnlyList<ApplicationResponse>> List(CancellationToken ct) =>
        await _db.Applications.OrderBy(a => a.Name)
            .Select(a => new ApplicationResponse(a.Id, a.Name, a.Description, a.CreatedUtc)).ToListAsync(ct);
}
