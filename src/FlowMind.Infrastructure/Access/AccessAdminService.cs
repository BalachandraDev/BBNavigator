using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.Infrastructure.Access;

public sealed record RuleDto(bool Everyone, IReadOnlyList<Guid> AudienceIds);
public sealed record SourceDto(string? Document, int? Page, Guid? DocumentId = null);
/// <summary>The pages of one document that an item was taken from ("4, 19").</summary>
public sealed record CitedIn(Guid DocumentId, string Pages);
public sealed record PageNode(Guid Id, string Name, string? Purpose, string Review, SourceDto Source, string? DocPages, IReadOnlyList<string> SharedWith, RuleDto? Rule)
{
    /// <summary>Every document the page draws on, with its own page numbers there. A page that draws on two documents is listed under both.</summary>
    public IReadOnlyList<CitedIn> Cited { get; init; } = Array.Empty<CitedIn>();
}
public sealed record ModuleNode(Guid? Id, string Name, string? Description, string? Review, SourceDto Source, RuleDto? Rule, IReadOnlyList<PageNode> Pages);
public sealed record DocNode(Guid Id, string Name, int PageCount, int OtherPages, string? OtherPageList, RuleDto? Rule);
/// <param name="Undecided">Modules, pages and document leftovers that have no rule yet (only full-access keys see them).</param>
/// <param name="OpenLinks">Active share links of this application with no audience: they answer from everything approved, so the rules do not apply to them.</param>
/// <param name="OpenKeys">Active access keys with no audience (full access), same reason.</param>
public sealed record AccessOverview(IReadOnlyList<ModuleNode> Modules, IReadOnlyList<DocNode> Documents, int Undecided, int Decisions, IReadOnlyList<string> OpenLinks, IReadOnlyList<string> OpenKeys);

public sealed record PreviewModule(string Name, IReadOnlyList<string> Pages);
public sealed record AccessPreview(string Audience, IReadOnlyList<PreviewModule> Modules, int OpenPages, int TotalPages, int OpenDocumentPages, int TotalDocumentPages, int OpenWorkflows, int TotalWorkflows);

public sealed record RuleChange(string Kind, Guid TargetId, string Mode, IReadOnlyList<Guid>? AudienceIds);
public sealed record RoleFound(string Role, int Mentions);

public sealed class AccessChangeException : Exception { public AccessChangeException(string message) : base(message) { } }

/// <summary>Everything the "Access rules" page needs: the tree to tick, previews, suggestions, and the one place rule changes are validated and saved.</summary>
public sealed class AccessAdminService
{
    public const int MaxChanges = 1000;
    private readonly FlowMindDbContext _db;
    public AccessAdminService(FlowMindDbContext db) => _db = db;

    private static RuleDto? Dto(AccessModel m, string kind, Guid id) =>
        m.Rules.TryGetValue((kind, id), out var r) ? new RuleDto(r.Everyone, r.Audiences.ToList()) : null;

    /// <summary>"4–6, 9" style list of page numbers.</summary>
    internal static string Ranges(IEnumerable<int> pages)
    {
        var p = pages.Distinct().OrderBy(x => x).ToList();
        if (p.Count == 0) return "";
        var parts = new List<string>();
        for (int i = 0; i < p.Count;)
        {
            int j = i;
            while (j + 1 < p.Count && p[j + 1] == p[j] + 1) j++;
            parts.Add(j > i ? $"{p[i]}–{p[j]}" : $"{p[i]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    public async Task<AccessOverview> OverviewAsync(Guid tenantId, Guid applicationId, CancellationToken ct)
    {
        var m = await AccessModel.LoadAsync(_db, tenantId, ct);
        var docNames = m.Documents.ToDictionary(d => d.Id, d => d.Name);
        var allDocNames = await _db.Documents.IgnoreQueryFilters().Where(d => d.TenantId == tenantId).ToDictionaryAsync(d => d.Id, d => d.FileName, ct);
        string? DocName(Guid? id) => id == null ? null : docNames.GetValueOrDefault(id.Value) ?? allDocNames.GetValueOrDefault(id.Value);
        var moduleNames = m.Modules.ToDictionary(x => x.Id, x => x.Name);
        var pageNames = m.Pages.ToDictionary(x => x.Id, x => x.Name);
        string Name(Owner o) => o.Kind switch
        {
            "module" => moduleNames.GetValueOrDefault(o.Id, "a module"),
            "page" => pageNames.GetValueOrDefault(o.Id, "a page"),
            _ => "a workflow"
        };

        // Which document pages each extracted page / module was taken from, and who else shares those pages.
        var claimed = new Dictionary<Owner, List<(Guid Doc, int Page)>>();
        foreach (var (key, owners) in m.Claims)
            foreach (var o in owners)
            {
                if (!claimed.TryGetValue(o, out var l)) claimed[o] = l = new();
                l.Add(key);
            }

        PageNode PageNodeOf(AccessModel.PageInfo p)
        {
            var mine = new Owner("page", p.Id);
            var spots = claimed.GetValueOrDefault(mine) ?? new();
            var docPages = spots.Count == 0 ? null : string.Join("; ", spots.GroupBy(s => s.Doc).Take(2).Select(g =>
                $"{(spots.Select(s => s.Doc).Distinct().Count() > 1 ? DocName(g.Key) + " " : "")}p. {Ranges(g.Select(s => s.Page))}"));
            // Other pages (or another module) that cite the same document pages. A page's own module is not "sharing": it simply contains the page.
            var ownModule = p.ModuleId is { } mid ? new Owner("module", mid) : (Owner?)null;
            var shared = spots.SelectMany(s => m.Claims[(s.Doc, s.Page)]).Where(o => o != mine && o.Kind != "workflow" && o != ownModule).Select(Name).Distinct().Take(3).ToList();
            return new PageNode(p.Id, p.Name, p.Purpose, p.Review.ToString(), new SourceDto(DocName(p.DocumentId), p.Page, p.DocumentId), docPages, shared, Dto(m, "page", p.Id))
            {
                Cited = spots.GroupBy(s => s.Doc).Select(g => new CitedIn(g.Key, Ranges(g.Select(s => s.Page)))).ToList()
            };
        }

        var pages = m.Pages.Where(p => p.ApplicationId == applicationId).OrderBy(p => p.Name).ToList();
        var modules = m.Modules.Where(x => x.ApplicationId == applicationId).OrderBy(x => x.Name).ToList();
        var nodes = modules.Select(mod => new ModuleNode(mod.Id, mod.Name, mod.Description, mod.Review.ToString(), new SourceDto(DocName(mod.DocumentId), mod.Page, mod.DocumentId),
            Dto(m, "module", mod.Id), pages.Where(p => p.ModuleId == mod.Id).Select(PageNodeOf).ToList())).ToList();
        var loose = pages.Where(p => p.ModuleId == null || modules.All(x => x.Id != p.ModuleId)).Select(PageNodeOf).ToList();
        if (loose.Count > 0) nodes.Add(new ModuleNode(null, "(No module)", null, null, new SourceDto(null, null), null, loose));

        var docs = m.Documents.Where(d => d.ApplicationId == applicationId).OrderBy(d => d.Name).Select(d =>
        {
            var other = m.UnclaimedPages(d).ToList();
            return new DocNode(d.Id, d.Name, d.Pages.Count, other.Count, other.Count == 0 ? null : Ranges(other), Dto(m, "document", d.Id));
        }).ToList();

        var undecided = nodes.Where(n => n.Id != null && n.Rule == null).Count()
            + nodes.SelectMany(n => n.Pages).Count(p => p.Rule == null)
            + docs.Count(d => d.OtherPages > 0 && d.Rule == null);
        var decisions = nodes.Count(n => n.Id != null) + nodes.Sum(n => n.Pages.Count) + docs.Count(d => d.OtherPages > 0);
        // Rules only apply to a key or link that has an audience. Say which ones do not have one, so "I set the rules but the bot still answers everything" is visible.
        var openLinks = await _db.BotLinks.Where(l => l.TenantId == tenantId && l.ApplicationId == applicationId && l.IsActive && l.AudienceId == null).OrderBy(l => l.Name).Select(l => l.Name).ToListAsync(ct);
        var openKeys = await _db.AccessKeys.Where(k => k.TenantId == tenantId && k.IsActive && k.AudienceId == null).OrderBy(k => k.Name).Select(k => k.Name).ToListAsync(ct);
        return new AccessOverview(nodes, docs, undecided, decisions, openLinks, openKeys);
    }

    /// <summary>What an audience would see, computed with exactly the logic chat and the knowledge pages use.</summary>
    public async Task<AccessPreview?> PreviewAsync(Guid tenantId, Guid audienceId, Guid applicationId, CancellationToken ct)
    {
        var audience = await _db.Audiences.Where(a => a.Id == audienceId && a.TenantId == tenantId).Select(a => a.Name).FirstOrDefaultAsync(ct);
        if (audience == null) return null;
        var m = await AccessModel.LoadAsync(_db, tenantId, ct);
        var scope = AccessResolver.Build(m, audienceId, audience);

        var pages = m.Pages.Where(p => p.ApplicationId == applicationId).ToList();
        var mods = m.Modules.Where(x => x.ApplicationId == applicationId).OrderBy(x => x.Name).ToList();
        var shown = mods.Where(x => scope.Modules.Contains(x.Id) || pages.Any(p => p.ModuleId == x.Id && scope.AppPages.Contains(p.Id)))
            .Select(x => new PreviewModule(x.Name, pages.Where(p => p.ModuleId == x.Id && scope.AppPages.Contains(p.Id)).Select(p => p.Name).OrderBy(n => n).ToList())).ToList();
        var loose = pages.Where(p => (p.ModuleId == null || mods.All(x => x.Id != p.ModuleId)) && scope.AppPages.Contains(p.Id)).Select(p => p.Name).OrderBy(n => n).ToList();
        if (loose.Count > 0) shown.Add(new PreviewModule("(No module)", loose));

        var docs = m.Documents.Where(d => d.ApplicationId == applicationId).ToList();
        var openDocPages = docs.Sum(d => d.Pages.Count(p => scope.Pages!.Allows(d.Id, p)));
        var wfs = m.Workflows.Where(w => w.ApplicationId == applicationId).ToList();
        return new AccessPreview(audience, shown, pages.Count(p => scope.AppPages.Contains(p.Id)), pages.Count, openDocPages, docs.Sum(d => d.Pages.Count),
            wfs.Count(w => scope.Workflows.Contains(w.Id)), wfs.Count);
    }

    public async Task<SuggestionResult> SuggestAsync(Guid tenantId, Guid applicationId, CancellationToken ct)
    {
        var audiences = await _db.Audiences.Where(a => a.TenantId == tenantId).Select(a => new AudienceTerms(a.Id, a.Name, a.Aliases)).ToListAsync(ct);
        var facts = await _db.RolePermissions.Where(r => r.TenantId == tenantId && r.ApplicationId == applicationId && r.Review != ReviewStatus.Rejected && r.AppPageId != null)
            .Select(r => new { r.AppPageId, r.Role, r.Permission }).ToListAsync(ct);
        return AccessSuggester.Suggest(facts.Select(f => (f.AppPageId!.Value, f.Role, f.Permission)), audiences);
    }

    /// <summary>Role names found in the documents that match none of the audiences yet: candidates for new audiences.</summary>
    public async Task<IReadOnlyList<RoleFound>> RolesFoundAsync(Guid tenantId, CancellationToken ct)
    {
        var audiences = await _db.Audiences.Where(a => a.TenantId == tenantId).Select(a => new AudienceTerms(a.Id, a.Name, a.Aliases)).ToListAsync(ct);
        var roles = await _db.RolePermissions.Where(r => r.TenantId == tenantId && r.Review != ReviewStatus.Rejected).Select(r => r.Role).ToListAsync(ct);
        return roles.Select(r => System.Text.RegularExpressions.Regex.Replace(r ?? "", @"\s+", " ").Trim())
            .Where(r => r.Length is >= 2 and <= 60)
            .GroupBy(r => AccessSuggester.Normalize(r))
            .Where(g => g.Key.Length >= 2 && AccessSuggester.Match(g.First(), audiences).Count == 0)
            .Where(g => !System.Text.RegularExpressions.Regex.IsMatch(g.Key, @"^(all|any|every)\b|^everyone$|^anyone$|^user$|^users$"))
            .Select(g => new RoleFound(g.GroupBy(x => x).OrderByDescending(x => x.Count()).First().Key, g.Count()))
            .OrderByDescending(r => r.Mentions).ThenBy(r => r.Role).Take(20).ToList();
    }

    /// <summary>Validate and save a batch of rule changes. Everything or nothing: any bad id rejects the whole batch.</summary>
    public async Task<int> ApplyAsync(Guid tenantId, IReadOnlyList<RuleChange> changes, CancellationToken ct)
    {
        if (changes.Count == 0) return 0;
        if (changes.Count > MaxChanges) throw new AccessChangeException($"At most {MaxChanges} changes can be saved at once.");

        foreach (var c in changes)
        {
            if (!AccessTargetKind.IsValid(c.Kind)) throw new AccessChangeException("Unknown kind of item.");
            if (c.Mode is not ("everyone" or "audiences" or "clear")) throw new AccessChangeException("Unknown mode.");
        }
        // The last change for an item wins.
        var final = changes.GroupBy(c => (c.Kind, c.TargetId)).Select(g => g.Last()).ToList();

        var wantedAudiences = final.Where(c => c.Mode == "audiences").SelectMany(c => c.AudienceIds ?? Array.Empty<Guid>()).Distinct().ToList();
        var validAudiences = (await _db.Audiences.Where(a => a.TenantId == tenantId && wantedAudiences.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        if (wantedAudiences.Any(a => !validAudiences.Contains(a))) throw new AccessChangeException("One of the chosen audiences does not exist.");

        async Task EnsureExist(string kind, IQueryable<Guid> existing)
        {
            var ids = final.Where(c => c.Kind == kind).Select(c => c.TargetId).Distinct().ToList();
            if (ids.Count == 0) return;
            var found = (await existing.Where(id => ids.Contains(id)).ToListAsync(ct)).ToHashSet();
            if (ids.Any(i => !found.Contains(i))) throw new AccessChangeException("One of the items does not exist (it may have been replaced by a newer upload). Reload the page.");
        }
        await EnsureExist(AccessTargetKind.Module, _db.Modules.Where(x => x.TenantId == tenantId).Select(x => x.Id));
        await EnsureExist(AccessTargetKind.Page, _db.AppPages.Where(x => x.TenantId == tenantId).Select(x => x.Id));
        await EnsureExist(AccessTargetKind.Document, _db.Documents.Where(x => x.TenantId == tenantId).Select(x => x.Id));

        var targets = final.Select(c => c.TargetId).Distinct().ToList();
        var rules = await _db.AccessRules.Where(r => r.TenantId == tenantId && targets.Contains(r.TargetId)).ToListAsync(ct);
        var ruleIds = rules.Select(r => r.Id).ToList();
        var links = await _db.AccessRuleAudiences.Where(l => l.TenantId == tenantId && ruleIds.Contains(l.RuleId)).ToListAsync(ct);

        foreach (var c in final)
        {
            var rule = rules.FirstOrDefault(r => r.TargetKind == c.Kind && r.TargetId == c.TargetId);
            if (c.Mode == "clear")
            {
                if (rule == null) continue;
                _db.AccessRuleAudiences.RemoveRange(links.Where(l => l.RuleId == rule.Id));
                _db.AccessRules.Remove(rule);
                continue;
            }
            if (rule == null)
            {
                rule = new AccessRule { TenantId = tenantId, TargetKind = c.Kind, TargetId = c.TargetId };
                _db.AccessRules.Add(rule);
            }
            rule.Everyone = c.Mode == "everyone";
            rule.UpdatedUtc = DateTime.UtcNow;
            // Change only the difference, so a row is never deleted and re-added under the same key in one save.
            var wanted = c.Mode == "audiences" ? (c.AudienceIds ?? Array.Empty<Guid>()).ToHashSet() : new HashSet<Guid>();
            var have = links.Where(l => l.RuleId == rule.Id).ToList();
            _db.AccessRuleAudiences.RemoveRange(have.Where(l => !wanted.Contains(l.AudienceId)));
            foreach (var a in wanted.Where(a => have.All(l => l.AudienceId != a)))
                _db.AccessRuleAudiences.Add(new AccessRuleAudience { TenantId = tenantId, RuleId = rule.Id, AudienceId = a });
        }
        await _db.SaveChangesAsync(ct);
        AccessResolver.Invalidate(tenantId);
        return final.Count;
    }
}
