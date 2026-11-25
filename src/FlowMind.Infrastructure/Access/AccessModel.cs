using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.Infrastructure.Access;

/// <summary>Something an access rule can be attached to. Kind is "module", "page", "document" or "workflow" (a workflow is decided by its module and pages).</summary>
public readonly record struct Owner(string Kind, Guid Id);

public sealed record RuleInfo(bool Everyone, IReadOnlySet<Guid> Audiences)
{
    public bool Allows(Guid audienceId) => Everyone || Audiences.Contains(audienceId);
}

/// <summary>
/// Everything needed to decide who sees what, loaded once for one company: the extracted modules, pages and workflows, the saved rules, the
/// searchable documents with their pages, and which document pages each extracted item was taken from (its "claim" on that page).
/// A document page is open to an audience only when every item that claims it is open to that audience.
/// </summary>
public sealed class AccessModel
{
    public sealed record ModuleInfo(Guid Id, Guid ApplicationId, string Name, string? Description, ReviewStatus Review, Guid? DocumentId, int? Page);
    public sealed record PageInfo(Guid Id, Guid ApplicationId, Guid? ModuleId, string Name, string? Purpose, ReviewStatus Review, Guid? DocumentId, int? Page);
    public sealed record WorkflowInfo(Guid Id, Guid ApplicationId, Guid? ModuleId, string Name, IReadOnlyList<Guid> StepPages);
    public sealed record DocInfo(Guid Id, Guid ApplicationId, string Name, IReadOnlyList<int> Pages);

    public IReadOnlyList<ModuleInfo> Modules { get; private set; } = Array.Empty<ModuleInfo>();
    public IReadOnlyList<PageInfo> Pages { get; private set; } = Array.Empty<PageInfo>();
    public IReadOnlyList<WorkflowInfo> Workflows { get; private set; } = Array.Empty<WorkflowInfo>();
    public IReadOnlyList<DocInfo> Documents { get; private set; } = Array.Empty<DocInfo>();
    public IReadOnlyDictionary<(string Kind, Guid Id), RuleInfo> Rules { get; private set; } = new Dictionary<(string, Guid), RuleInfo>();
    /// <summary>(document, page) → the extracted items that were taken from that page.</summary>
    public IReadOnlyDictionary<(Guid Doc, int Page), List<Owner>> Claims { get; private set; } = new Dictionary<(Guid, int), List<Owner>>();

    private readonly Dictionary<Guid, WorkflowInfo> _workflows = new();

    public static async Task<AccessModel> LoadAsync(FlowMindDbContext db, Guid tenantId, CancellationToken ct)
    {
        var m = new AccessModel();

        // Searchable documents: approved, not deleted, with the pages of their current version.
        var docs = await db.Documents.Where(d => d.TenantId == tenantId && d.Status == DocumentStatus.Approved && d.CurrentVersionId != null)
            .Select(d => new { d.Id, d.ApplicationId, d.FileName, Version = d.CurrentVersionId!.Value }).ToListAsync(ct);
        var versionIds = docs.Select(d => d.Version).ToList();
        var pageRows = versionIds.Count == 0 ? new List<(Guid DocumentVersionId, int PageNumber)>()
            : (await db.DocumentPages.Where(p => p.TenantId == tenantId && versionIds.Contains(p.DocumentVersionId))
                .Select(p => new { p.DocumentVersionId, p.PageNumber }).ToListAsync(ct)).Select(p => (p.DocumentVersionId, p.PageNumber)).ToList();
        var pagesByVersion = pageRows.GroupBy(p => p.DocumentVersionId).ToDictionary(g => g.Key, g => g.Select(x => x.PageNumber).Distinct().OrderBy(x => x).ToList());
        m.Documents = docs.Select(d => new DocInfo(d.Id, d.ApplicationId, d.FileName, pagesByVersion.GetValueOrDefault(d.Version) ?? new List<int>())).ToList();

        // Any version (old or current) maps to its document, so items extracted from an earlier upload still count.
        var versionToDoc = (await db.DocumentVersions.Where(v => v.TenantId == tenantId).Select(v => new { v.Id, v.DocumentId }).ToListAsync(ct))
            .ToDictionary(v => v.Id, v => v.DocumentId);

        var claims = new Dictionary<(Guid, int), List<Owner>>();
        void Claim(Guid? version, int? page, string kind, Guid? id)
        {
            if (version == null || page == null || id == null || !versionToDoc.TryGetValue(version.Value, out var doc)) return;
            var key = (doc, page.Value);
            if (!claims.TryGetValue(key, out var list)) claims[key] = list = new List<Owner>();
            var owner = new Owner(kind, id.Value);
            if (!list.Contains(owner)) list.Add(owner);
        }
        Guid? DocOf(Guid? version) => version != null && versionToDoc.TryGetValue(version.Value, out var d) ? d : null;

        // Rejected items are ignored: they are not part of the knowledge anyone sees.
        var live = ReviewStatus.Rejected;
        var modules = await db.Modules.Where(x => x.TenantId == tenantId && x.Review != live)
            .Select(x => new { x.Id, x.ApplicationId, x.Name, x.Description, x.Review, x.SourceDocumentVersionId, x.SourcePage }).ToListAsync(ct);
        var pages = await db.AppPages.Where(x => x.TenantId == tenantId && x.Review != live)
            .Select(x => new { x.Id, x.ApplicationId, x.ModuleId, x.Name, x.Purpose, x.Review, x.SourceDocumentVersionId, x.SourcePage }).ToListAsync(ct);
        var workflows = await db.Workflows.Where(x => x.TenantId == tenantId && x.Review != live)
            .Select(x => new { x.Id, x.ApplicationId, x.ModuleId, x.Name, x.SourceDocumentVersionId, x.SourcePage }).ToListAsync(ct);
        var steps = await db.WorkflowSteps.Where(x => x.TenantId == tenantId && x.Review != live)
            .Select(x => new { x.WorkflowId, x.AppPageId, x.SourceDocumentVersionId, x.SourcePage }).ToListAsync(ct);

        foreach (var x in modules) Claim(x.SourceDocumentVersionId, x.SourcePage, "module", x.Id);
        foreach (var x in pages) Claim(x.SourceDocumentVersionId, x.SourcePage, "page", x.Id);
        foreach (var x in workflows) Claim(x.SourceDocumentVersionId, x.SourcePage, "workflow", x.Id);
        foreach (var x in steps) Claim(x.SourceDocumentVersionId, x.SourcePage, "workflow", x.WorkflowId);

        foreach (var x in await db.PageFunctionalities.Where(x => x.TenantId == tenantId && x.Review != live).Select(x => new { x.AppPageId, x.SourceDocumentVersionId, x.SourcePage }).ToListAsync(ct))
            Claim(x.SourceDocumentVersionId, x.SourcePage, "page", x.AppPageId);
        foreach (var x in await db.ValidationRules.Where(x => x.TenantId == tenantId && x.Review != live).Select(x => new { x.AppPageId, x.SourceDocumentVersionId, x.SourcePage }).ToListAsync(ct))
            Claim(x.SourceDocumentVersionId, x.SourcePage, "page", x.AppPageId);
        foreach (var x in await db.RolePermissions.Where(x => x.TenantId == tenantId && x.Review != live).Select(x => new { x.AppPageId, x.SourceDocumentVersionId, x.SourcePage }).ToListAsync(ct))
            Claim(x.SourceDocumentVersionId, x.SourcePage, "page", x.AppPageId);
        foreach (var x in await db.BusinessRules.Where(x => x.TenantId == tenantId && x.Review != live).Select(x => new { x.AppPageId, x.ModuleId, x.SourceDocumentVersionId, x.SourcePage }).ToListAsync(ct))
        {
            if (x.AppPageId != null) Claim(x.SourceDocumentVersionId, x.SourcePage, "page", x.AppPageId);
            else Claim(x.SourceDocumentVersionId, x.SourcePage, "module", x.ModuleId);
        }
        foreach (var x in await db.PageLinks.Where(x => x.TenantId == tenantId && x.Review != live).Select(x => new { x.FromPageId, x.SourceDocumentVersionId, x.SourcePage }).ToListAsync(ct))
            Claim(x.SourceDocumentVersionId, x.SourcePage, "page", x.FromPageId);
        foreach (var x in await db.WorkflowTransitions.Where(x => x.TenantId == tenantId && x.Review != live).Select(x => new { x.WorkflowId, x.SourceDocumentVersionId, x.SourcePage }).ToListAsync(ct))
            Claim(x.SourceDocumentVersionId, x.SourcePage, "workflow", x.WorkflowId);

        m.Claims = claims;
        m.Modules = modules.Select(x => new ModuleInfo(x.Id, x.ApplicationId, x.Name, x.Description, x.Review, DocOf(x.SourceDocumentVersionId), x.SourcePage)).ToList();
        m.Pages = pages.Select(x => new PageInfo(x.Id, x.ApplicationId, x.ModuleId, x.Name, x.Purpose, x.Review, DocOf(x.SourceDocumentVersionId), x.SourcePage)).ToList();
        m.Workflows = workflows.Select(w => new WorkflowInfo(w.Id, w.ApplicationId, w.ModuleId, w.Name,
            steps.Where(s => s.WorkflowId == w.Id && s.AppPageId != null).Select(s => s.AppPageId!.Value).Distinct().ToList())).ToList();
        foreach (var w in m.Workflows) m._workflows[w.Id] = w;

        var rules = await db.AccessRules.Where(r => r.TenantId == tenantId).ToListAsync(ct);
        var links = (await db.AccessRuleAudiences.Where(r => r.TenantId == tenantId).Select(r => new { r.RuleId, r.AudienceId }).ToListAsync(ct))
            .GroupBy(r => r.RuleId).ToDictionary(g => g.Key, g => (IReadOnlySet<Guid>)g.Select(r => r.AudienceId).ToHashSet());
        m.Rules = rules.ToDictionary(r => (r.TargetKind, r.TargetId),
            r => new RuleInfo(r.Everyone, links.GetValueOrDefault(r.Id) ?? new HashSet<Guid>()));
        return m;
    }

    /// <summary>Is this module / page / document / workflow open to the audience? No rule = not decided = closed.</summary>
    public bool IsOpen(Owner o, Guid audienceId)
    {
        if (o.Kind == "workflow")
        {
            if (!_workflows.TryGetValue(o.Id, out var w)) return false;
            // A workflow is shown only if its module is open and every page it walks through is open: a visible workflow never names a hidden page.
            var moduleOk = w.ModuleId != null ? Rule("module", w.ModuleId.Value, audienceId) : w.StepPages.Count > 0;
            return moduleOk && w.StepPages.All(p => Rule("page", p, audienceId));
        }
        return Rule(o.Kind, o.Id, audienceId);
    }

    private bool Rule(string kind, Guid id, Guid audienceId) => Rules.TryGetValue((kind, id), out var r) && r.Allows(audienceId);

    /// <summary>Is one document page open to the audience? Items that cite it decide; a page nothing cites follows the document's own rule.</summary>
    public bool IsPageOpen(Guid documentId, int page, Guid audienceId) =>
        Claims.TryGetValue((documentId, page), out var owners) && owners.Count > 0
            ? owners.All(o => IsOpen(o, audienceId))
            : Rule("document", documentId, audienceId);

    /// <summary>The document pages no extracted item cites (they follow the document's own rule).</summary>
    public IEnumerable<int> UnclaimedPages(DocInfo d) => d.Pages.Where(p => !Claims.TryGetValue((d.Id, p), out var o) || o.Count == 0);
}
