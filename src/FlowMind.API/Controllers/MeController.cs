using FlowMind.API.Authentication;
using FlowMind.Infrastructure.Access;
using FlowMind.Infrastructure.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowMind.API.Controllers;

/// <param name="CanBrowseKnowledge">Whether to show the Knowledge page. Always true for company admins and keys with no audience.</param>
public sealed record MyAccess(bool CanBrowseKnowledge, string Audience);

/// <summary>What the signed-in person's own access allows, so the app can hide pages they cannot use. The server still enforces every one of these.</summary>
[ApiController]
[Route("api/v1/me")]
[Authorize(Policy = "TenantMember")]
public class MeController : ControllerBase
{
    private readonly FlowMindDbContext _db;
    private readonly AccessResolver _access;
    public MeController(FlowMindDbContext db, AccessResolver access) { _db = db; _access = access; }

    [HttpGet("access")]
    public async Task<MyAccess> Access(CancellationToken ct)
    {
        var scope = await User.ResolveScopeAsync(_access, _db.CurrentTenantId ?? Guid.Empty, ct);
        return new MyAccess(scope.CanBrowseKnowledge, scope.Name);
    }
}
