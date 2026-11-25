using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Infrastructure.Access;
using FlowMind.Infrastructure.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.API.Controllers;

public sealed class AudienceRequest
{
    [Required, StringLength(80, MinimumLength = 2)] public string Name { get; set; } = "";
    /// <summary>Other words the documents use for this group, comma separated. Only used to suggest rules.</summary>
    [StringLength(300)] public string? Aliases { get; set; }
    [StringLength(300)] public string? Description { get; set; }
    /// <summary>True = people with this audience's key also get the Knowledge page. False (default) = the chat only.</summary>
    public bool CanBrowseKnowledge { get; set; }
}

public sealed class RuleChangeRequest
{
    [Required, StringLength(10)] public string Kind { get; set; } = "";
    [Required] public Guid TargetId { get; set; }
    /// <summary>"everyone", "audiences" (exactly the listed ones; none listed = admins only) or "clear" (undecided).</summary>
    [Required, RegularExpression("^(everyone|audiences|clear)$")] public string Mode { get; set; } = "";
    public List<Guid>? AudienceIds { get; set; }
}
public sealed class SaveRulesRequest
{
    [Required, MinLength(1), MaxLength(AccessAdminService.MaxChanges)] public List<RuleChangeRequest> Changes { get; set; } = new();
}

public sealed record AudienceSummary(Guid Id, string Name, string? Aliases, string? Description, int Keys, int Links, int Decisions, DateTime CreatedUtc, bool CanBrowseKnowledge = false);

/// <summary>
/// Company admins define audiences (Manager, Nurse, Ward ...) and decide which modules, pages and document leftovers each one may see.
/// Tenant comes from the token; every id in a request is checked against that company before anything is saved.
/// </summary>
[ApiController]
[Route("api/v1/access")]
[Authorize(Policy = "TenantAdmin")]
public class AccessRulesController : ControllerBase
{
    private const int MaxAudiences = 30;
    private readonly FlowMindDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IAuditLogger _audit;
    private readonly AccessAdminService _admin;
    public AccessRulesController(FlowMindDbContext db, ITenantContext tenant, IAuditLogger audit, AccessAdminService admin)
    { _db = db; _tenant = tenant; _audit = audit; _admin = admin; }

    private Guid TenantId => _tenant.TenantId!.Value;

    private static string? CleanAliases(string? aliases)
    {
        var parts = (aliases ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => Regex.Replace(a, @"[\p{C}]", " ").Trim()).Select(a => Regex.Replace(a, @"\s+", " "))
            .Where(a => a.Length is >= 2 and <= 40).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
    private static string CleanName(string name) => Regex.Replace(Regex.Replace(name, @"[\p{C}]", " "), @"\s+", " ").Trim();

    [HttpGet("audiences")]
    public async Task<IReadOnlyList<AudienceSummary>> Audiences(CancellationToken ct)
    {
        var audiences = await _db.Audiences.OrderBy(a => a.Name).ToListAsync(ct);
        var keys = (await _db.AccessKeys.Where(k => k.IsActive && k.AudienceId != null).GroupBy(k => k.AudienceId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.N);
        var links = (await _db.BotLinks.Where(l => l.AudienceId != null).GroupBy(l => l.AudienceId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.N);
        var rules = (await _db.AccessRuleAudiences.GroupBy(r => r.AudienceId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.N);
        return audiences.Select(a => new AudienceSummary(a.Id, a.Name, a.Aliases, a.Description, keys.GetValueOrDefault(a.Id), links.GetValueOrDefault(a.Id), rules.GetValueOrDefault(a.Id), a.CreatedUtc, a.CanBrowseKnowledge)).ToList();
    }

    [HttpPost("audiences")]
    public async Task<ActionResult<AudienceSummary>> CreateAudience(AudienceRequest req, CancellationToken ct)
    {
        var name = CleanName(req.Name);
        if (name.Length < 2) return Problem("Give the audience a name.", statusCode: 400);
        if (await _db.Audiences.CountAsync(ct) >= MaxAudiences) return Problem($"Limit of {MaxAudiences} audiences reached.", statusCode: 409);
        var lower = name.ToLower();
        if (await _db.Audiences.AnyAsync(a => a.Name.ToLower() == lower, ct)) return Problem("An audience with this name already exists.", statusCode: 409);   // case-insensitive, like the database's own unique index

        var a = new Audience { TenantId = TenantId, Name = name, Aliases = CleanAliases(req.Aliases), Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(), CanBrowseKnowledge = req.CanBrowseKnowledge };
        _db.Audiences.Add(a);
        await _db.SaveChangesAsync(ct);
        AccessResolver.Invalidate(TenantId);
        await _audit.LogAsync("audience.create", "Audience", a.Id.ToString(), a.Name, ct: ct);
        return Created($"/api/v1/access/audiences/{a.Id}", new AudienceSummary(a.Id, a.Name, a.Aliases, a.Description, 0, 0, 0, a.CreatedUtc, a.CanBrowseKnowledge));
    }

    [HttpPut("audiences/{id:guid}")]
    public async Task<IActionResult> UpdateAudience(Guid id, AudienceRequest req, CancellationToken ct)
    {
        var a = await _db.Audiences.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a == null) return NotFound();
        var name = CleanName(req.Name);
        if (name.Length < 2) return Problem("Give the audience a name.", statusCode: 400);
        var lower = name.ToLower();
        if (await _db.Audiences.AnyAsync(x => x.Name.ToLower() == lower && x.Id != id, ct)) return Problem("An audience with this name already exists.", statusCode: 409);
        a.Name = name; a.Aliases = CleanAliases(req.Aliases); a.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
        a.CanBrowseKnowledge = req.CanBrowseKnowledge;
        await _db.SaveChangesAsync(ct);
        AccessResolver.Invalidate(TenantId);
        await _audit.LogAsync("audience.update", "Audience", id.ToString(), a.Name, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// Delete an audience. Refused while an active key or a share link still uses it (it would otherwise silently get more or less access).
    /// Revoked keys are detached; the audience's ticks in the rules are removed.
    /// </summary>
    [HttpDelete("audiences/{id:guid}")]
    public async Task<IActionResult> DeleteAudience(Guid id, CancellationToken ct)
    {
        var a = await _db.Audiences.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a == null) return NotFound();
        var activeKeys = await _db.AccessKeys.CountAsync(k => k.AudienceId == id && k.IsActive, ct);
        var links = await _db.BotLinks.CountAsync(l => l.AudienceId == id, ct);
        if (activeKeys > 0 || links > 0)
            return Problem($"{a.Name} is still used by {activeKeys} key(s) and {links} share link(s). Move them to another audience (or full access) first.", statusCode: 409);

        foreach (var k in await _db.AccessKeys.Where(k => k.AudienceId == id).ToListAsync(ct)) k.AudienceId = null;   // revoked keys only: they cannot sign in anyway
        _db.AccessRuleAudiences.RemoveRange(await _db.AccessRuleAudiences.Where(r => r.AudienceId == id).ToListAsync(ct));
        _db.Audiences.Remove(a);
        await _db.SaveChangesAsync(ct);
        AccessResolver.Invalidate(TenantId);
        await _audit.LogAsync("audience.delete", "Audience", id.ToString(), a.Name, ct: ct);
        return NoContent();
    }

    /// <summary>The tree to tick: modules with their pages, the document leftovers, every saved rule and how many are still undecided.</summary>
    [HttpGet("overview")]
    public async Task<ActionResult<AccessOverview>> Overview([FromQuery, Required] Guid applicationId, CancellationToken ct)
    {
        if (!await _db.Applications.AnyAsync(a => a.Id == applicationId, ct)) return NotFound();
        return await _admin.OverviewAsync(TenantId, applicationId, ct);
    }

    /// <summary>Save a batch of rule changes (all or nothing).</summary>
    [HttpPut("rules")]
    public async Task<IActionResult> SaveRules(SaveRulesRequest req, CancellationToken ct)
    {
        try
        {
            var n = await _admin.ApplyAsync(TenantId, req.Changes.Select(c => new RuleChange(c.Kind, c.TargetId, c.Mode, c.AudienceIds)).ToList(), ct);
            await _audit.LogAsync("access.rules", "AccessRules", null, $"{n} change(s)", ct: ct);
            return Ok(new { saved = n });
        }
        catch (AccessChangeException ex) { return Problem(ex.Message, statusCode: 400); }
    }

    /// <summary>Suggested ticks read from the role facts already extracted from the documents. Nothing is saved.</summary>
    [HttpGet("suggestions")]
    public async Task<ActionResult<SuggestionResult>> Suggestions([FromQuery, Required] Guid applicationId, CancellationToken ct)
    {
        if (!await _db.Applications.AnyAsync(a => a.Id == applicationId, ct)) return NotFound();
        return await _admin.SuggestAsync(TenantId, applicationId, ct);
    }

    /// <summary>What an audience would be able to see right now (the same logic chat uses).</summary>
    [HttpGet("preview")]
    public async Task<ActionResult<AccessPreview>> Preview([FromQuery, Required] Guid audienceId, [FromQuery, Required] Guid applicationId, CancellationToken ct)
    {
        if (!await _db.Applications.AnyAsync(a => a.Id == applicationId, ct)) return NotFound();
        var p = await _admin.PreviewAsync(TenantId, audienceId, applicationId, ct);
        return p == null ? NotFound() : p;
    }

    /// <summary>Roles mentioned in the documents that no audience matches yet: one click to create them.</summary>
    [HttpGet("roles-found")]
    public async Task<IReadOnlyList<RoleFound>> RolesFound(CancellationToken ct) => await _admin.RolesFoundAsync(TenantId, ct);
}
