using System.Text.RegularExpressions;
using FlowMind.API.Authentication;
using FlowMind.Application.DTOs;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Database;
using FlowMind.Infrastructure.Knowledge;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.API.Controllers;

/// <summary>Read the extracted application knowledge (modules, pages, workflows). All queries are tenant-filtered.</summary>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = "TenantMember")]
public class KnowledgeController : ControllerBase, Microsoft.AspNetCore.Mvc.Filters.IAsyncActionFilter
{
    private readonly FlowMindDbContext _db;
    private readonly FlowMind.Infrastructure.Access.AccessResolver _access;
    public KnowledgeController(FlowMindDbContext db, FlowMind.Infrastructure.Access.AccessResolver access) { _db = db; _access = access; }

    /// <summary>
    /// A user key whose audience is chat-only (the default) cannot browse knowledge at all: the Knowledge page is hidden for them, and this makes the server refuse too,
    /// so hiding a tab is never the only protection.
    /// </summary>
    async Task Microsoft.AspNetCore.Mvc.Filters.IAsyncActionFilter.OnActionExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context, Microsoft.AspNetCore.Mvc.Filters.ActionExecutionDelegate next)
    {
        if (User.IsInRole(FlowMind.API.Authentication.Roles.TenantUser))
        {
            var scope = await ScopeAsync(context.HttpContext.RequestAborted);
            if (!scope.CanBrowseKnowledge)
            {
                context.Result = new ObjectResult(new ProblemDetails { Status = 403, Title = "Browsing knowledge is not enabled for your access. You can use the chat." }) { StatusCode = 403 };
                return;
            }
        }
        await next();
    }

    /// <summary>What the caller's audience may see. Company admins and keys without an audience see everything.</summary>
    private Task<FlowMind.Infrastructure.Access.AccessScope> ScopeAsync(CancellationToken ct) =>
        User.ResolveScopeAsync(_access, _db.CurrentTenantId ?? Guid.Empty, ct);

    /// <summary>Maps source document version ids to document names, for citations.</summary>
    private async Task<Dictionary<Guid, string>> SourceNamesAsync(IEnumerable<Guid?> versionIds, CancellationToken ct)
    {
        var ids = versionIds.Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        if (ids.Count == 0) return new();
        var versions = await _db.DocumentVersions.Where(v => ids.Contains(v.Id)).Select(v => new { v.Id, v.DocumentId }).ToListAsync(ct);
        var docIds = versions.Select(v => v.DocumentId).Distinct().ToList();
        var docs = await _db.Documents.IgnoreQueryFilters().Where(d => docIds.Contains(d.Id) && (_db.CurrentTenantId == null || d.TenantId == _db.CurrentTenantId))
            .Select(d => new { d.Id, d.FileName }).ToListAsync(ct);
        var names = docs.ToDictionary(d => d.Id, d => d.FileName);
        return versions.Where(v => names.ContainsKey(v.DocumentId)).ToDictionary(v => v.Id, v => names[v.DocumentId]);
    }

    private static SourceRef Src(Dictionary<Guid, string> names, Guid? versionId, int? page) =>
        new(versionId != null && names.TryGetValue(versionId.Value, out var n) ? n : null, page);

    /// <summary>Ordinary users only ever see reviewed (Approved) knowledge. Admins also see unreviewed drafts.</summary>
    private ReviewStatus[] Visible(bool includeRejected = false) =>
        User.IsInRole(FlowMind.API.Authentication.Roles.TenantUser) ? new[] { ReviewStatus.Approved }
        : includeRejected ? new[] { ReviewStatus.Pending, ReviewStatus.Approved, ReviewStatus.Rejected }
        : new[] { ReviewStatus.Pending, ReviewStatus.Approved };

    [HttpGet("modules")]
    public async Task<ActionResult<ModulesResponse>> Modules([FromQuery, System.ComponentModel.DataAnnotations.Required] Guid applicationId,
        [FromQuery] bool includeRejected = false, CancellationToken ct = default)
    {
        var visible = Visible(includeRejected);
        var scope = await ScopeAsync(ct);
        var mods = (await _db.Modules.Where(m => m.ApplicationId == applicationId && visible.Contains(m.Review))
            .OrderBy(m => m.Name).ToListAsync(ct)).Where(m => scope.AllowsModule(m.Id)).ToList();
        var pages = (await _db.AppPages.Where(p => p.ApplicationId == applicationId && visible.Contains(p.Review))
            .OrderBy(p => p.Name).ToListAsync(ct)).Where(p => scope.AllowsPage(p.Id)).ToList();
        var names = await SourceNamesAsync(mods.Select(m => m.SourceDocumentVersionId), ct);

        static PageBrief B(AppPage p) => new(p.Id, p.Name, p.Purpose, p.Review.ToString());
        var result = mods.Select(m => new ModuleDto(m.Id, m.Name, m.Description, m.Review.ToString(),
            Src(names, m.SourceDocumentVersionId, m.SourcePage), pages.Where(p => p.ModuleId == m.Id).Select(B).ToList())).ToList();

        var loose = pages.Where(p => p.ModuleId == null || mods.All(m => m.Id != p.ModuleId)).Select(B).ToList();
        if (loose.Count > 0) result.Add(new ModuleDto(null, "(No module)", null, null, new SourceRef(null, null), loose));
        return new ModulesResponse(result);
    }

    [HttpGet("pages/{id:guid}")]
    public async Task<ActionResult<PageDetailDto>> Page(Guid id, CancellationToken ct)
    {
        var visible = Visible();
        var scope = await ScopeAsync(ct);
        var p = await _db.AppPages.FirstOrDefaultAsync(x => x.Id == id && visible.Contains(x.Review), ct);
        if (p == null || !scope.AllowsPage(p.Id)) return NotFound();   // a page the audience may not see looks exactly like a page that does not exist

        var funcs = await _db.PageFunctionalities.Where(x => x.AppPageId == id && visible.Contains(x.Review)).ToListAsync(ct);
        var vals = await _db.ValidationRules.Where(x => x.AppPageId == id && visible.Contains(x.Review)).ToListAsync(ct);
        var rules = await _db.BusinessRules.Where(x => x.AppPageId == id && visible.Contains(x.Review)).ToListAsync(ct);
        var roles = await _db.RolePermissions.Where(x => x.AppPageId == id && visible.Contains(x.Review)).ToListAsync(ct);
        var links = await _db.PageLinks.Where(x => (x.FromPageId == id || x.ToPageId == id) && visible.Contains(x.Review)).ToListAsync(ct);
        var otherIds = links.Select(l => l.FromPageId == id ? l.ToPageId : l.FromPageId).Distinct().ToList();
        var others = (await _db.AppPages.Where(x => otherIds.Contains(x.Id) && visible.Contains(x.Review)).ToListAsync(ct))
            .Where(x => scope.AllowsPage(x.Id)).ToDictionary(x => x.Id, x => x.Name);
        links = links.Where(l => others.ContainsKey(l.FromPageId == id ? l.ToPageId : l.FromPageId)).ToList(); // never link to a hidden page
        var module = p.ModuleId == null || !scope.AllowsModule(p.ModuleId.Value) ? null : (await _db.Modules.FirstOrDefaultAsync(m => m.Id == p.ModuleId, ct))?.Name;

        var names = await SourceNamesAsync(new[] { p.SourceDocumentVersionId }
            .Concat(funcs.Select(x => x.SourceDocumentVersionId)).Concat(vals.Select(x => x.SourceDocumentVersionId))
            .Concat(rules.Select(x => x.SourceDocumentVersionId)).Concat(roles.Select(x => x.SourceDocumentVersionId)), ct);

        NextPageDto N(PageLink l, Guid other) => new(l.Id, other, others.GetValueOrDefault(other, "(unknown)"), l.Condition, l.Review.ToString());
        return new PageDetailDto(p.Id, p.Name, p.Purpose, module, p.Review.ToString(), Src(names, p.SourceDocumentVersionId, p.SourcePage),
            funcs.Select(x => new TextItemDto(x.Id, x.Description, x.Review.ToString(), Src(names, x.SourceDocumentVersionId, x.SourcePage))).ToList(),
            vals.Select(x => new ValidationDto(x.Id, x.FieldName, x.RuleText, x.Review.ToString(), Src(names, x.SourceDocumentVersionId, x.SourcePage))).ToList(),
            rules.Select(x => new TextItemDto(x.Id, x.Statement, x.Review.ToString(), Src(names, x.SourceDocumentVersionId, x.SourcePage))).ToList(),
            roles.Select(x => new RoleDto(x.Id, x.Role, x.Permission, x.Review.ToString(), Src(names, x.SourceDocumentVersionId, x.SourcePage))).ToList(),
            links.Where(l => l.FromPageId == id).Select(l => N(l, l.ToPageId)).ToList(),
            links.Where(l => l.ToPageId == id).Select(l => N(l, l.FromPageId)).ToList());
    }

    [HttpGet("workflows")]
    public async Task<IReadOnlyList<WorkflowBrief>> Workflows([FromQuery, System.ComponentModel.DataAnnotations.Required] Guid applicationId, CancellationToken ct)
    {
        var visible = Visible();
        var scope = await ScopeAsync(ct);
        var wfs = (await _db.Workflows.Where(w => w.ApplicationId == applicationId && visible.Contains(w.Review)).OrderBy(w => w.Name).ToListAsync(ct))
            .Where(w => scope.AllowsWorkflow(w.Id)).ToList();
        var ids = wfs.Select(w => w.Id).ToList();
        var counts = await _db.WorkflowSteps.Where(s => ids.Contains(s.WorkflowId) && visible.Contains(s.Review)).GroupBy(s => s.WorkflowId)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var mods = (await _db.Modules.Where(m => m.ApplicationId == applicationId).ToListAsync(ct)).Where(m => scope.AllowsModule(m.Id)).ToDictionary(m => m.Id, m => m.Name);
        return wfs.Select(w => new WorkflowBrief(w.Id, w.Name, w.ModuleId != null ? mods.GetValueOrDefault(w.ModuleId.Value) : null,
            w.Description, w.Review.ToString(), counts.GetValueOrDefault(w.Id))).ToList();
    }

    [HttpGet("workflows/{id:guid}")]
    public async Task<ActionResult<WorkflowDetailDto>> Workflow(Guid id, CancellationToken ct)
    {
        var visible = Visible();
        var scope = await ScopeAsync(ct);
        var w = await _db.Workflows.FirstOrDefaultAsync(x => x.Id == id && visible.Contains(x.Review), ct);
        if (w == null || !scope.AllowsWorkflow(w.Id)) return NotFound();
        var steps = await _db.WorkflowSteps.Where(s => s.WorkflowId == id && visible.Contains(s.Review)).OrderBy(s => s.Order).ToListAsync(ct);
        var trans = await _db.WorkflowTransitions.Where(t => t.WorkflowId == id && visible.Contains(t.Review)).ToListAsync(ct);
        var pageIds = steps.Where(s => s.AppPageId != null).Select(s => s.AppPageId!.Value).Distinct().ToList();
        var pageNames = await _db.AppPages.Where(p => pageIds.Contains(p.Id) && visible.Contains(p.Review)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var module = w.ModuleId == null || !scope.AllowsModule(w.ModuleId.Value) ? null : (await _db.Modules.FirstOrDefaultAsync(m => m.Id == w.ModuleId, ct))?.Name;
        var names = await SourceNamesAsync(new[] { w.SourceDocumentVersionId }, ct);
        var stepNames = steps.ToDictionary(s => s.Id, s => s.Name);

        return new WorkflowDetailDto(w.Id, w.Name, module, w.Description, w.Review.ToString(), Src(names, w.SourceDocumentVersionId, w.SourcePage),
            steps.Select(s => new WorkflowStepDto(s.Id, s.Order, s.Name, s.Description, s.AppPageId,
                s.AppPageId != null ? pageNames.GetValueOrDefault(s.AppPageId.Value) : null)).ToList(),
            trans.Where(t => stepNames.ContainsKey(t.FromStepId) && stepNames.ContainsKey(t.ToStepId))
                .Select(t => new WorkflowTransitionDto(t.Id, t.FromStepId, stepNames[t.FromStepId], t.ToStepId, stepNames[t.ToStepId], t.Condition)).ToList());
    }
}

/// <summary>Human review: list extracted facts, correct them, approve or reject them. Only Approved knowledge is trusted for answers.</summary>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = "TenantAdmin")]
public class ReviewController : ControllerBase
{
    private readonly FlowMindDbContext _db;
    private readonly IAuditLogger _audit;
    public ReviewController(FlowMindDbContext db, IAuditLogger audit) { _db = db; _audit = audit; }

    [HttpGet("review")]
    public async Task<IReadOnlyList<ReviewItemDto>> List([FromQuery, System.ComponentModel.DataAnnotations.Required] Guid applicationId,
        [FromQuery] string status = "Pending", [FromQuery] Guid? documentId = null, CancellationToken ct = default)
    {
        var st = Enum.TryParse<ReviewStatus>(status, true, out var parsed) ? parsed : ReviewStatus.Pending;

        Guid? versionId = null;
        if (documentId != null)
        {
            var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
            versionId = doc?.CurrentVersionId ?? Guid.Empty; // unknown/no version → matches nothing
        }

        var raw = new List<(string Type, KnowledgeItem Item, string Title, string? Text)>();
        void Add<T>(string type, List<T> items, Func<T, string> title, Func<T, string?> text) where T : KnowledgeItem =>
            raw.AddRange(items.Select(i => (type, (KnowledgeItem)i, title(i), text(i))));

        IQueryable<T> Q<T>(IQueryable<T> q) where T : KnowledgeItem =>
            q.Where(x => x.ApplicationId == applicationId && x.Review == st && (versionId == null || x.SourceDocumentVersionId == versionId))
             .OrderBy(x => x.CreatedUtc).Take(200);

        var pageNames = await _db.AppPages.Where(p => p.ApplicationId == applicationId).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        string P(Guid? id) => id != null && pageNames.TryGetValue(id.Value, out var n) ? n : "?";

        Add("module", await Q(_db.Modules).ToListAsync(ct), x => x.Name, x => x.Description);
        Add("page", await Q(_db.AppPages).ToListAsync(ct), x => x.Name, x => x.Purpose);
        Add("functionality", await Q(_db.PageFunctionalities).ToListAsync(ct), x => P(x.AppPageId), x => x.Description);
        Add("validation", await Q(_db.ValidationRules).ToListAsync(ct), x => $"{P(x.AppPageId)}{(x.FieldName != null ? " / " + x.FieldName : "")}", x => x.RuleText);
        Add("businessrule", await Q(_db.BusinessRules).ToListAsync(ct), x => x.AppPageId != null ? P(x.AppPageId) : "Business rule", x => x.Statement);
        Add("role", await Q(_db.RolePermissions).ToListAsync(ct), x => x.Role, x => x.Permission);
        Add("workflow", await Q(_db.Workflows).ToListAsync(ct), x => x.Name, x => x.Description);

        var names = await SourceNamesAsync(raw.Select(r => r.Item.SourceDocumentVersionId), ct);
        return raw.Select(r => new ReviewItemDto(r.Type, r.Item.Id, r.Title, r.Text, r.Item.Review.ToString(),
            new SourceRef(r.Item.SourceDocumentVersionId != null && names.TryGetValue(r.Item.SourceDocumentVersionId.Value, out var n) ? n : null, r.Item.SourcePage))).ToList();
    }

    private async Task<Dictionary<Guid, string>> SourceNamesAsync(IEnumerable<Guid?> versionIds, CancellationToken ct)
    {
        var ids = versionIds.Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        if (ids.Count == 0) return new();
        var versions = await _db.DocumentVersions.Where(v => ids.Contains(v.Id)).Select(v => new { v.Id, v.DocumentId }).ToListAsync(ct);
        var docIds = versions.Select(v => v.DocumentId).Distinct().ToList();
        var docs = await _db.Documents.IgnoreQueryFilters()
            .Where(d => docIds.Contains(d.Id) && (_db.CurrentTenantId == null || d.TenantId == _db.CurrentTenantId))
            .ToDictionaryAsync(d => d.Id, d => d.FileName, ct);
        return versions.Where(v => docs.ContainsKey(v.DocumentId)).ToDictionary(v => v.Id, v => docs[v.DocumentId]);
    }

    /// <summary>Correct and/or approve/reject one extracted item. Type: module, page, functionality, validation, businessrule, role, workflow.</summary>
    [HttpPut("review/{type}/{id:guid}")]
    public async Task<IActionResult> Update(string type, Guid id, ReviewUpdateRequest req, CancellationToken ct)
    {
        var status = Enum.Parse<ReviewStatus>(req.Status);
        var name = req.Name is null ? null : Regex.Replace(req.Name.Trim(), @"\s+", " ");
        var text = req.Text?.Trim();
        if (text is { Length: 0 }) text = null;

        KnowledgeItem? item;
        switch (type.ToLowerInvariant())
        {
            case "module":
                var m = await _db.Modules.FirstOrDefaultAsync(x => x.Id == id, ct); item = m;
                if (m != null) { if (name != null) { m.Name = name; m.NormalizedName = KnowledgeWriter.Normalize(name); } if (text != null) m.Description = text; }
                break;
            case "page":
                var p = await _db.AppPages.FirstOrDefaultAsync(x => x.Id == id, ct); item = p;
                if (p != null) { if (name != null) { p.Name = name; p.NormalizedName = KnowledgeWriter.Normalize(name); } if (text != null) p.Purpose = text; }
                break;
            case "functionality":
                var f = await _db.PageFunctionalities.FirstOrDefaultAsync(x => x.Id == id, ct); item = f;
                if (f != null && text != null) f.Description = text;
                break;
            case "validation":
                var v = await _db.ValidationRules.FirstOrDefaultAsync(x => x.Id == id, ct); item = v;
                if (v != null) { if (name != null) v.FieldName = name; if (text != null) v.RuleText = text; }
                break;
            case "businessrule":
                var b = await _db.BusinessRules.FirstOrDefaultAsync(x => x.Id == id, ct); item = b;
                if (b != null && text != null) b.Statement = text;
                break;
            case "role":
                var r = await _db.RolePermissions.FirstOrDefaultAsync(x => x.Id == id, ct); item = r;
                if (r != null) { if (name != null) r.Role = name; if (text != null) r.Permission = text; }
                break;
            case "workflow":
                var w = await _db.Workflows.FirstOrDefaultAsync(x => x.Id == id, ct); item = w;
                if (w != null) { if (name != null) { w.Name = name; w.NormalizedName = KnowledgeWriter.Normalize(name); } if (text != null) w.Description = text; }
                break;
            default:
                return Problem("Unknown type. Use: module, page, functionality, validation, businessrule, role, workflow.", statusCode: 400);
        }
        if (item == null) return NotFound();

        item.Review = status;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("review.update", type, id.ToString(), $"{status}{(name != null || text != null ? " (edited)" : "")}", ct: ct);
        return NoContent();
    }

    /// <summary>Approve everything still pending that was extracted from this document's current version.</summary>
    [HttpPost("documents/{id:guid}/approve")]
    public async Task<ActionResult<ApproveDocumentResponse>> ApproveDocument(Guid id, CancellationToken ct)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc?.CurrentVersionId == null) return NotFound();
        if (doc.Status is not (DocumentStatus.PendingReview or DocumentStatus.Approved))
            return Problem($"Document is {doc.Status}; analysis must finish before approval.", statusCode: 409);

        var v = doc.CurrentVersionId;
        int total = 0;
        async Task<int> Approve<T>(IQueryable<T> set) where T : KnowledgeItem =>
            await set.Where(x => x.SourceDocumentVersionId == v && x.Review == ReviewStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Review, ReviewStatus.Approved), ct);

        total += await Approve(_db.Modules); total += await Approve(_db.AppPages); total += await Approve(_db.PageFunctionalities);
        total += await Approve(_db.ValidationRules); total += await Approve(_db.BusinessRules); total += await Approve(_db.RolePermissions);
        total += await Approve(_db.PageLinks); total += await Approve(_db.Workflows); total += await Approve(_db.WorkflowSteps);
        total += await Approve(_db.WorkflowTransitions);

        doc.Status = DocumentStatus.Approved;
        // Approved documents become searchable: queue indexing (chunk + embed) unless one is already queued/running.
        if (!await _db.ProcessingJobs.AnyAsync(j => j.DocumentVersionId == v && j.Stage == JobStage.Embedding
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct))
            _db.ProcessingJobs.Add(new DocumentProcessingJob { TenantId = doc.TenantId, DocumentVersionId = v!.Value, Stage = JobStage.Embedding });
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("document.approve", "Document", id.ToString(), $"{total} items", ct: ct);
        return new ApproveDocumentResponse(total);
    }
}
