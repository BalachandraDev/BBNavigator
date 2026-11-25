using System.ComponentModel.DataAnnotations;
using FlowMind.Application.Interfaces;
using FlowMind.Infrastructure.Database;
using FlowMind.Infrastructure.Insights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.API.Controllers;

public sealed class GapStateRequest
{
    [Required] public Guid ApplicationId { get; set; }
    [Required, StringLength(64, MinimumLength = 64)] public string Key { get; set; } = "";
    /// <summary>"handled", "ignored", or "open" (forget the earlier decision).</summary>
    [Required, RegularExpression("^(handled|ignored|open)$")] public string Status { get; set; } = "";
}

/// <summary>What the admin can learn from the chats: questions the documents could not answer, and a period report. Company admins only.</summary>
[ApiController]
[Route("api/v1/insights")]
[Authorize(Policy = "TenantAdmin")]
public class InsightsController : ControllerBase
{
    private readonly FlowMindDbContext _db;
    private readonly InsightsService _insights;
    private readonly IAuditLogger _audit;
    public InsightsController(FlowMindDbContext db, InsightsService insights, IAuditLogger audit) { _db = db; _insights = insights; _audit = audit; }

    private Task<bool> Mine(Guid applicationId, CancellationToken ct) => _db.Applications.AnyAsync(a => a.Id == applicationId, ct);

    /// <summary>Questions the assistant could not answer in the last N days, grouped, most asked first, with the closest document sections.</summary>
    [HttpGet("gaps")]
    public async Task<ActionResult<GapReport>> Gaps([FromQuery, Required] Guid applicationId, [FromQuery] int days = 30, CancellationToken ct = default)
        => await Mine(applicationId, ct) ? await _insights.GapsAsync(applicationId, days, ct) : NotFound();

    /// <summary>Mark an unanswered question handled (the documentation was fixed) or ignored. A question asked again afterwards reopens.</summary>
    [HttpPost("gaps/state")]
    public async Task<IActionResult> SetGap(GapStateRequest req, CancellationToken ct)
    {
        if (!await Mine(req.ApplicationId, ct)) return NotFound();
        if (!await _insights.SetGapStateAsync(req.ApplicationId, req.Key, req.Status, ct)) return Problem("That question reference is not valid.", statusCode: 400);
        await _audit.LogAsync("insights.gap-" + req.Status, "Application", req.ApplicationId.ToString(), ct: ct);
        return NoContent();
    }

    [HttpGet("usage")]
    public async Task<ActionResult<UsageReport>> Usage([FromQuery, Required] Guid applicationId, [FromQuery] int days = 30, CancellationToken ct = default)
        => await _insights.UsageAsync(applicationId, days, ct) is { } r ? r : NotFound();
}

public sealed record SupportItem(Guid Id, string Channel, string Question, bool WasAnswered, string? Note, string? Contact, string Status, string? Resolution, DateTime CreatedUtc, DateTime? ClosedUtc);
public sealed record SupportList(int OpenTotal, IReadOnlyList<SupportItem> Items);
public sealed class SupportPatch
{
    [RegularExpression("^(open|done)$")] public string? Status { get; set; }
    [StringLength(500)] public string? Resolution { get; set; }
}

/// <summary>The inbox of "send this to support" requests. Company admins only: it holds what the senders chose to share, including contacts.</summary>
[ApiController]
[Route("api/v1/support-requests")]
[Authorize(Policy = "TenantAdmin")]
public class SupportRequestsController : ControllerBase
{
    private readonly FlowMindDbContext _db;
    private readonly IAuditLogger _audit;
    public SupportRequestsController(FlowMindDbContext db, IAuditLogger audit) { _db = db; _audit = audit; }

    [HttpGet]
    public async Task<SupportList> List([FromQuery, Required] Guid applicationId, [FromQuery] string status = "open", CancellationToken ct = default)
    {
        var q = _db.SupportRequests.Where(s => s.ApplicationId == applicationId);
        if (status is "open" or "done") q = q.Where(s => s.Status == status);
        var rows = await q.OrderByDescending(s => s.CreatedUtc).Take(200).ToListAsync(ct);
        return new SupportList(await _db.SupportRequests.CountAsync(s => s.Status == "open", ct), rows.Select(Map).ToList());
    }

    /// <summary>How many requests are waiting, for the badge in the menu.</summary>
    [HttpGet("count")]
    public async Task<object> Count(CancellationToken ct) => new { open = await _db.SupportRequests.CountAsync(s => s.Status == "open", ct) };

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<SupportItem>> Update(Guid id, SupportPatch p, CancellationToken ct)
    {
        var s = await _db.SupportRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s == null) return NotFound();
        if (p.Status != null && p.Status != s.Status) { s.Status = p.Status; s.ClosedUtc = p.Status == "done" ? DateTime.UtcNow : null; }
        if (p.Resolution != null) s.Resolution = string.IsNullOrWhiteSpace(p.Resolution) ? null : p.Resolution.Trim();
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("support." + (s.Status == "done" ? "close" : "update"), "SupportRequest", s.Id.ToString(), ct: ct);
        return Map(s);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var s = await _db.SupportRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s == null) return NotFound();
        _db.SupportRequests.Remove(s);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("support.delete", "SupportRequest", id.ToString(), ct: ct);
        return NoContent();
    }

    private static SupportItem Map(FlowMind.Domain.Entities.SupportRequest s) =>
        new(s.Id, s.Channel, s.Question, s.WasAnswered, s.Note, s.Contact, s.Status, s.Resolution, s.CreatedUtc, s.ClosedUtc);
}
