using System.Text;
using System.Text.RegularExpressions;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.Infrastructure.Chat;

/// <summary>
/// Turns the reviewed workflows of an application (which an administrator has approved) into a short, clean, ordered
/// outline the model can read next to the document excerpts. Questions such as "what do I do next?" or "what comes after
/// dispensing?" are about ORDER, and order is exactly what a workflow records; in the raw document it is often buried in a
/// table that the PDF turned into scattered lines. Only Approved workflows, steps and pages are used.
/// </summary>
public static class WorkflowOutline
{
    private const int MaxChars = 700;
    private const int MaxWorkflows = 2;

    private static readonly Regex Procedural = new(
        @"\b(next|after|before|then|step|steps|flow|process|workflow|procedure|order|sequence|first|last|finish|finished|complete|completed|done|start|begin|proceed|move on|what happens|what do i do|what should i do|how do i|go to)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Tamil cues for order questions: next / after / before / first / last / steps / what to do / how. (No \b: Tamil vowel signs are not word characters.)
    private static readonly Regex TamilProcedural = new(
        @"அடுத்து|அடுத்தது|அடுத்த\s+படி|பிறகு|பின்பு|பின்னர்|அதன்\s+பின்|முன்பு|முன்னர்|முந்தைய|முதலில்|கடைசியில்|படிகள்|படி\s+என்ன|செயல்முறை|என்ன\s+செய்ய|எப்படி",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool LooksProcedural(string question) => Procedural.IsMatch(question) || TamilProcedural.IsMatch(question);

    private static readonly Regex Forward = new(
        @"\b(next|after|afterwards|then|what now|what do i do|what should i do|proceed|move on)\b|அடுத்து|அடுத்தது|அடுத்த\s+படி|பிறகு|பின்பு|பின்னர்|அதன்\s+பின்",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Backward = new(@"\b(before|previous|prior|preceding)\b|முன்பு|முன்னர்|முந்தைய", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Words that describe the question, not the step the person is at.
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "next", "after", "afterwards", "before", "then", "step", "steps", "flow", "process", "workflow", "procedure", "order", "sequence", "first", "last",
        "finish", "finished", "complete", "completed", "done", "start", "begin", "proceed", "move", "happens", "what", "should", "how", "page", "screen", "now",
        "which", "where", "does", "come", "comes", "previous", "prior", "preceding", "there", "need", "anything", "else", "have", "has", "had", "was", "were",
        "been", "are", "the", "and", "for", "with", "can", "you", "your", "our", "not", "from", "this", "that", "into", "about", "who", "why", "when", "will",
    };

    private static HashSet<string> Words(string? s) =>
        Regex.Matches(s ?? "", @"[\p{L}\p{N}]{3,}").Select(m => m.Value.ToLowerInvariant()).Where(w => w != "and" && w != "the" && w != "for" && w != "with").ToHashSet();

    /// <summary>Same word allowing endings: "collected" ~ "collection", "stickers" ~ "sticker", "reviewed" ~ "review". Short words must match exactly.</summary>
    private static bool Same(string a, string b)
    {
        if (a == b) return true;
        var n = Math.Min(a.Length, b.Length); var p = 0;
        while (p < n && a[p] == b[p]) p++;
        return p >= 5 && p >= n - 3;
    }

    /// <summary>
    /// For "what next / what comes before" questions: find the step the person says they are at, in every reviewed workflow, and name the
    /// neighbouring step. Returns null when it is not clear which step is meant (the outline alone is then given).
    /// </summary>
    internal static string? Navigate(string question, IReadOnlyList<(Workflow Workflow, List<WorkflowStep> Steps, int Score)> scored,
        IReadOnlyDictionary<Guid, string> pageNames, out HashSet<Guid> workflowIds)
    {
        workflowIds = new HashSet<Guid>();
        var back = Backward.IsMatch(question) && !Forward.IsMatch(question);
        if (!back && !Forward.IsMatch(question)) return null;
        var terms = Words(question).Where(t => !Filler.Contains(t)).ToList();
        if (terms.Count == 0) return null;

        string PageOf(WorkflowStep s) => s.AppPageId != null && pageNames.TryGetValue(s.AppPageId.Value, out var pn) ? pn : s.Name;
        // The same step across workflows: same name, or the same page (labels differ a little: "Dispensing" / "Dispensing (receive and weigh)").
        static string Plain(string s) => Regex.Replace(s.ToLowerInvariant(), @"\s*\(.*?\)", "").Trim();
        bool SameStep(WorkflowStep a, WorkflowStep b) =>
            Plain(a.Name) == Plain(b.Name) || Plain(PageOf(a)) == Plain(PageOf(b)) || (a.AppPageId != null && a.AppPageId == b.AppPageId);
        List<T> Distinct<T>(IEnumerable<T> items, Func<T, WorkflowStep> step)
        {
            var kept = new List<T>();
            foreach (var it in items) if (!kept.Any(k => SameStep(step(k), step(it)))) kept.Add(it);
            return kept;
        }

        // Best-matching step of each workflow: most of its words mentioned, then the largest share of its words ("Dispensing" beats "Open Dispensing").
        var best = new List<(Workflow W, List<WorkflowStep> Steps, int Idx, int Matched, double Cover)>();
        foreach (var x in scored)
        {
            (int Idx, int Matched, double Cover)? top = null;
            for (int i = 0; i < x.Steps.Count; i++)
            {
                var toks = Words(x.Steps[i].Name + " " + PageOf(x.Steps[i]));
                var matched = toks.Count(t => terms.Any(q => Same(q, t)));
                if (matched == 0) continue;
                var cover = matched / (double)toks.Count;
                if (top == null || matched > top.Value.Matched || (matched == top.Value.Matched && cover > top.Value.Cover)) top = (i, matched, cover);
            }
            if (top != null) best.Add((x.Workflow, x.Steps, top.Value.Idx, top.Value.Matched, top.Value.Cover));
        }
        if (best.Count == 0) return null;
        var lead = best.OrderByDescending(b => b.Matched).ThenByDescending(b => b.Cover).First();
        var winners = best.Where(b => b.Matched == lead.Matched && Math.Abs(b.Cover - lead.Cover) < 1e-9).ToList();
        if (Distinct(winners, w => w.Steps[w.Idx]).Count > 1) return null;   // two different steps fit equally well: do not guess

        string Describe(WorkflowStep s)
        {
            var page = PageOf(s);
            var pageNote = string.Equals(page, s.Name, StringComparison.OrdinalIgnoreCase) ? "" : $" (page: {page})";
            var what = string.IsNullOrWhiteSpace(s.Description) ? "" : " - " + (s.Description.Length > 90 ? s.Description[..90].TrimEnd() + "…" : s.Description.TrimEnd('.'));
            return $"\"{s.Name}\"{pageNote}{what}";
        }

        var current = winners[0].Steps[winners[0].Idx];
        var moves = winners.Select(w => (w.W, Neighbour: w.Idx + (back ? -1 : 1) is var j && j >= 0 && j < w.Steps.Count ? w.Steps[j] : null)).ToList();
        foreach (var w in winners) workflowIds.Add(w.W.Id);

        var withNeighbour = moves.Where(m => m.Neighbour != null).ToList();
        var label = back ? "PREVIOUS STEP" : "NEXT STEP";
        if (withNeighbour.Count == 0)
            return back ? $"FIRST STEP: \"{current.Name}\" is the first step of \"{winners[0].W.Name}\", so nothing comes before it."
                        : $"LAST STEP: \"{current.Name}\" is the final step of \"{winners[0].W.Name}\", so nothing comes after it.";

        var distinct = Distinct(withNeighbour, m => m.Neighbour!);
        if (distinct.Count == 1)
            return $"{label}: {(back ? "before" : "after")} \"{current.Name}\" the {(back ? "previous" : "next")} step is {Describe(distinct[0].Neighbour!)}. This is the answer to which step is {(back ? "before" : "next")}.";
        return $"{label} (the reviewed workflows differ): {(back ? "before" : "after")} \"{current.Name}\" " + string.Join("; ", distinct.Select(m => $"in \"{m.W.Name}\" it is {Describe(m.Neighbour!)}")) + ".";
    }

    /// <param name="documentIds">
    /// A share link may be limited to certain documents. Then only workflows extracted from those documents may be used, exactly like the
    /// document excerpts: otherwise steps from a document the link is not mapped to would reach the model (and the visitor).
    /// </param>
    public static async Task<PackedSource?> BuildAsync(FlowMindDbContext db, Guid applicationId, string question, CancellationToken ct, IReadOnlyCollection<Guid>? documentIds = null, string? contextHint = null, Access.AccessScope? access = null)
    {
        var query = db.Workflows.Where(w => w.ApplicationId == applicationId && w.Review == ReviewStatus.Approved);
        // A restricted audience only gets workflows its rules open (module and every page the workflow walks through).
        if (access is { Unrestricted: false })
        {
            var open = access.Workflows.ToList();
            query = query.Where(w => open.Contains(w.Id));
        }
        if (documentIds != null)
        {
            var scope = documentIds.ToList();
            var versions = await db.DocumentVersions.Where(v => scope.Contains(v.DocumentId)).Select(v => v.Id).ToListAsync(ct);
            query = query.Where(w => w.SourceDocumentVersionId != null && versions.Contains(w.SourceDocumentVersionId.Value));
        }
        var workflows = await query.OrderBy(w => w.Name).Take(30).ToListAsync(ct);
        if (workflows.Count == 0) return null;

        var ids = workflows.Select(w => w.Id).ToList();
        var steps = await db.WorkflowSteps.Where(s => ids.Contains(s.WorkflowId) && s.Review == ReviewStatus.Approved)
            .OrderBy(s => s.Order).ToListAsync(ct);
        var pageIds = steps.Where(s => s.AppPageId != null).Select(s => s.AppPageId!.Value).Distinct().ToList();
        var pageNames = await db.AppPages.Where(p => pageIds.Contains(p.Id) && p.Review == ReviewStatus.Approved).ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        // A Tamil question has no words in common with (English) workflow names; then the retrieved excerpts stand in for it.
        var terms = SourcePacker.Terms(string.IsNullOrWhiteSpace(contextHint) ? question : contextHint);
        var procedural = LooksProcedural(question);

        // A workflow is relevant when the question uses words from its name, its steps or its pages.
        var scored = workflows.Select(w =>
        {
            var own = steps.Where(s => s.WorkflowId == w.Id).ToList();
            var text = w.Name + " " + string.Join(" ", own.Select(s => s.Name + " " + (s.AppPageId != null && pageNames.TryGetValue(s.AppPageId.Value, out var pn) ? pn : "")));
            return (Workflow: w, Steps: own, Score: terms.Count(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)));
        }).Where(x => x.Steps.Count > 0).ToList();

        var chosen = procedural
            ? scored.Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(MaxWorkflows).ToList()
            : scored.Where(x => x.Score >= 2).OrderByDescending(x => x.Score).Take(1).ToList();
        if (chosen.Count == 0 && procedural && scored.Count <= 3) chosen = scored.Take(MaxWorkflows).ToList();   // few workflows and an "order" question: show them

        // "I did X, what next?": the server finds X and its neighbour itself, so the model does not have to read an arrow chain correctly.
        var navigation = Navigate(question, scored, pageNames, out var navWorkflows);
        if (navigation != null) chosen = scored.Where(x => navWorkflows.Contains(x.Workflow.Id)).OrderBy(x => x.Workflow.Name).Take(MaxWorkflows).ToList();
        if (chosen.Count == 0) return null;

        var sb = new StringBuilder("WORKFLOW OUTLINE (reviewed steps, in order)\n");
        if (navigation != null) sb.AppendLine(navigation);
        var used = 0;
        foreach (var (w, own, _) in chosen)
        {
            var line = new StringBuilder($"{w.Name}: ");
            line.Append(string.Join(" -> ", own.Select(s =>
            {
                var page = s.AppPageId != null && pageNames.TryGetValue(s.AppPageId.Value, out var pn) && !string.Equals(pn, s.Name, StringComparison.OrdinalIgnoreCase) ? $" (page: {pn})" : "";
                var what = string.IsNullOrWhiteSpace(s.Description) ? "" : " - " + (s.Description.Length > 90 ? s.Description[..90].TrimEnd() + "…" : s.Description);
                return $"{s.Order}. {s.Name}{page}{what}";
            })));
            if (used + line.Length > MaxChars) break;
            used += line.Length;
            sb.AppendLine(line.ToString());
        }
        var textOut = sb.ToString().TrimEnd();
        if (textOut.Length <= 50) return null;

        // Cite the page the workflow was extracted from.
        var first = chosen[0].Workflow;
        var version = first.SourceDocumentVersionId == null ? null : await db.DocumentVersions.FirstOrDefaultAsync(v => v.Id == first.SourceDocumentVersionId, ct);
        var doc = version == null ? null : await db.Documents.FirstOrDefaultAsync(d => d.Id == version.DocumentId, ct);
        if (doc == null) return null;
        return new PackedSource(new ChunkHit(Guid.NewGuid(), doc.Id, doc.FileName, first.SourcePage is > 0 ? first.SourcePage.Value : 1, textOut, 1.0, 1.0), textOut);
    }
}
