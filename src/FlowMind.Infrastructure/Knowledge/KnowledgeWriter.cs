using System.Text.RegularExpressions;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.Infrastructure.Knowledge;

public sealed record ApplyStats(int Modules, int Pages, int Details, int Workflows, int SkippedReviewedWorkflows);

/// <summary>
/// Turns validated AI output into linked knowledge rows for one application. Idempotent: re-applying the same
/// output creates nothing new (rows are matched by normalized name/text). New rows are Pending until a human reviews them.
/// AI text is only ever stored as data; it never drives any query or action.
/// </summary>
public sealed class KnowledgeWriter
{
    private const int MaxName = 200, MaxText = 1000, MaxItemsPerList = 50;

    private readonly FlowMindDbContext _db;
    public KnowledgeWriter(FlowMindDbContext db) => _db = db;

    public static string Normalize(string? s) =>
        Regex.Replace((s ?? "").Trim().ToLowerInvariant(), @"\s+", " ");

    private static string Clean(string? s, int max)
    {
        var t = Regex.Replace(s ?? "", @"[\p{C}]", " ").Trim();
        t = Regex.Replace(t, @"\s+", " ");
        return t.Length > max ? t[..max] : t;
    }

    public async Task<ApplyStats> ApplyAsync(Guid tenantId, Guid applicationId, Guid versionId, IEnumerable<AiAnalysis> results, CancellationToken ct)
    {
        var modules = await _db.Modules.Where(x => x.ApplicationId == applicationId).ToListAsync(ct);
        var pages = await _db.AppPages.Where(x => x.ApplicationId == applicationId).ToListAsync(ct);
        var funcs = await _db.PageFunctionalities.Where(x => x.ApplicationId == applicationId).ToListAsync(ct);
        var vals = await _db.ValidationRules.Where(x => x.ApplicationId == applicationId).ToListAsync(ct);
        var rules = await _db.BusinessRules.Where(x => x.ApplicationId == applicationId).ToListAsync(ct);
        var roles = await _db.RolePermissions.Where(x => x.ApplicationId == applicationId).ToListAsync(ct);
        var links = await _db.PageLinks.Where(x => x.ApplicationId == applicationId).ToListAsync(ct);
        var workflows = await _db.Workflows.Where(x => x.ApplicationId == applicationId).ToListAsync(ct);
        var steps = await _db.WorkflowSteps.Where(x => x.ApplicationId == applicationId).ToListAsync(ct);
        var transitions = await _db.WorkflowTransitions.Where(x => x.ApplicationId == applicationId).ToListAsync(ct);

        int nMod = 0, nPage = 0, nDetail = 0, nWf = 0, skipped = 0;

        T Stamp<T>(T item, int? page) where T : KnowledgeItem
        {
            item.TenantId = tenantId; item.ApplicationId = applicationId;
            item.SourceDocumentVersionId = versionId; item.SourcePage = page is > 0 ? page : null;
            return item;
        }

        Module? GetModule(string? name, int? page, string? description = null)
        {
            var n = Clean(name, MaxName);
            if (n.Length == 0) return null;
            var norm = Normalize(n);
            var m = modules.FirstOrDefault(x => x.NormalizedName == norm);
            if (m == null)
            {
                m = Stamp(new Module { Name = n, NormalizedName = norm }, page);
                modules.Add(m); _db.Modules.Add(m); nMod++;
            }
            if (m.Review == ReviewStatus.Pending && string.IsNullOrEmpty(m.Description) && Clean(description, MaxText) is { Length: > 0 } d)
                m.Description = d;
            return m;
        }

        // reference = the page is only mentioned (next page, workflow step), not described. A reference never creates a
        // second copy of a page that exists under another module, and a new stub has no module until it is described.
        AppPage? GetPage(string? name, Guid? moduleId, int? page, bool reference = false)
        {
            var n = Clean(name, MaxName);
            if (n.Length == 0) return null;
            var norm = Normalize(n);
            var matches = pages.Where(x => x.NormalizedName == norm).ToList();
            var p = matches.FirstOrDefault(x => x.ModuleId == moduleId)
                    ?? (reference ? matches.FirstOrDefault()
                                  : matches.Count == 1 && (moduleId == null || matches[0].ModuleId == null) ? matches[0] : null);
            if (p == null)
            {
                p = Stamp(new AppPage { Name = n, NormalizedName = norm, ModuleId = reference ? null : moduleId }, page);
                pages.Add(p); _db.AppPages.Add(p); nPage++;
            }
            else if (p.ModuleId == null && moduleId != null && p.Review == ReviewStatus.Pending) p.ModuleId = moduleId;
            return p;
        }

        foreach (var r in results)
        {
            foreach (var m in (r.Modules ?? new()).Take(MaxItemsPerList))
                GetModule(m.Name, null, m.Description);

            foreach (var ap in (r.Pages ?? new()).Take(MaxItemsPerList))
            {
                var mod = GetModule(ap.Module, ap.SourcePage);
                var page = GetPage(ap.Name, mod?.Id, ap.SourcePage);
                if (page == null) continue;
                if (page.Review == ReviewStatus.Pending && string.IsNullOrEmpty(page.Purpose) && Clean(ap.Purpose, MaxText) is { Length: > 0 } purpose)
                {
                    page.Purpose = purpose;
                    // A page first seen only as a reference is cited where it is actually described.
                    if (ap.SourcePage is > 0) { page.SourcePage = ap.SourcePage; page.SourceDocumentVersionId = versionId; }
                }

                foreach (var f in (ap.Functionalities ?? new()).Take(MaxItemsPerList))
                {
                    var t = Clean(f, MaxText);
                    if (t.Length == 0 || funcs.Any(x => x.AppPageId == page.Id && Normalize(x.Description) == Normalize(t))) continue;
                    var e = Stamp(new PageFunctionality { AppPageId = page.Id, Description = t }, ap.SourcePage);
                    funcs.Add(e); _db.PageFunctionalities.Add(e); nDetail++;
                }
                foreach (var v in (ap.Validations ?? new()).Take(MaxItemsPerList))
                {
                    var rule = Clean(v.Rule, MaxText); var field = Clean(v.Field, MaxName);
                    if (rule.Length == 0 || vals.Any(x => x.AppPageId == page.Id && Normalize(x.RuleText) == Normalize(rule) && Normalize(x.FieldName) == Normalize(field))) continue;
                    var e = Stamp(new ValidationRule { AppPageId = page.Id, FieldName = field.Length == 0 ? null : field, RuleText = rule }, ap.SourcePage);
                    vals.Add(e); _db.ValidationRules.Add(e); nDetail++;
                }
                foreach (var b in (ap.BusinessRules ?? new()).Take(MaxItemsPerList))
                {
                    var t = Clean(b, MaxText);
                    if (t.Length == 0 || rules.Any(x => x.AppPageId == page.Id && Normalize(x.Statement) == Normalize(t))) continue;
                    var e = Stamp(new BusinessRule { AppPageId = page.Id, ModuleId = page.ModuleId, Statement = t }, ap.SourcePage);
                    rules.Add(e); _db.BusinessRules.Add(e); nDetail++;
                }
                foreach (var ro in (ap.Roles ?? new()).Take(MaxItemsPerList))
                {
                    var role = Clean(ro.Role, MaxName); var perm = Clean(ro.Permission, 500);
                    if (role.Length == 0 || perm.Length == 0 || roles.Any(x => x.AppPageId == page.Id && Normalize(x.Role) == Normalize(role) && Normalize(x.Permission) == Normalize(perm))) continue;
                    var e = Stamp(new RolePermission { AppPageId = page.Id, Role = role, Permission = perm }, ap.SourcePage);
                    roles.Add(e); _db.RolePermissions.Add(e); nDetail++;
                }
                foreach (var nx in (ap.NextPages ?? new()).Take(MaxItemsPerList))
                {
                    var target = GetPage(nx.Page, page.ModuleId, ap.SourcePage, reference: true);
                    if (target == null || target.Id == page.Id) continue;
                    var cond = Clean(nx.Condition, 500);
                    if (links.Any(x => x.FromPageId == page.Id && x.ToPageId == target.Id && Normalize(x.Condition) == Normalize(cond))) continue;
                    var e = Stamp(new PageLink { FromPageId = page.Id, ToPageId = target.Id, Condition = cond.Length == 0 ? null : cond }, ap.SourcePage);
                    links.Add(e); _db.PageLinks.Add(e); nDetail++;
                }
            }

            foreach (var aw in (r.Workflows ?? new()).Take(MaxItemsPerList))
            {
                var name = Clean(aw.Name, MaxName);
                if (name.Length == 0) continue;
                var mod = GetModule(aw.Module, aw.SourcePage);
                var norm = Normalize(name);
                var wf = workflows.FirstOrDefault(x => x.NormalizedName == norm);
                if (wf == null)
                {
                    wf = Stamp(new Workflow { Name = name, NormalizedName = norm, ModuleId = mod?.Id, Description = Clean(aw.Description, MaxText) }, aw.SourcePage);
                    workflows.Add(wf); _db.Workflows.Add(wf); nWf++;
                }
                else if (wf.Review != ReviewStatus.Pending) { skipped++; continue; } // reviewed workflows are frozen

                var wfSteps = steps.Where(s => s.WorkflowId == wf.Id).ToList();
                var ordered = (aw.Steps ?? new()).Take(MaxItemsPerList)
                    .Select((s, i) => (Step: s, Idx: i)).OrderBy(x => x.Step.Order ?? int.MaxValue).ThenBy(x => x.Idx).Select(x => x.Step);
                foreach (var s in ordered)
                {
                    var sn = Clean(s.Name, MaxName);
                    if (sn.Length == 0 || wfSteps.Any(x => x.NormalizedName == Normalize(sn))) continue;
                    var pg = GetPage(s.Page, mod?.Id, aw.SourcePage, reference: true);
                    var st = Stamp(new WorkflowStep
                    {
                        WorkflowId = wf.Id, Name = sn, NormalizedName = Normalize(sn),
                        Order = wfSteps.Count == 0 ? 1 : wfSteps.Max(x => x.Order) + 1,
                        AppPageId = pg?.Id, Description = Clean(s.Description, MaxText)
                    }, aw.SourcePage);
                    wfSteps.Add(st); steps.Add(st); _db.WorkflowSteps.Add(st); nDetail++;
                }
                foreach (var t in (aw.Transitions ?? new()).Take(MaxItemsPerList))
                {
                    var from = wfSteps.FirstOrDefault(x => x.NormalizedName == Normalize(t.From));
                    var to = wfSteps.FirstOrDefault(x => x.NormalizedName == Normalize(t.To));
                    if (from == null || to == null || from.Id == to.Id) continue; // only link steps that really exist
                    var cond = Clean(t.Condition, 500);
                    if (transitions.Any(x => x.WorkflowId == wf.Id && x.FromStepId == from.Id && x.ToStepId == to.Id && Normalize(x.Condition) == Normalize(cond))) continue;
                    var e = Stamp(new WorkflowTransition { WorkflowId = wf.Id, FromStepId = from.Id, ToStepId = to.Id, Condition = cond.Length == 0 ? null : cond }, aw.SourcePage);
                    transitions.Add(e); _db.WorkflowTransitions.Add(e); nDetail++;
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        return new ApplyStats(nMod, nPage, nDetail, nWf, skipped);
    }
}
