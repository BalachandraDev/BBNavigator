using System.Text.RegularExpressions;

namespace FlowMind.Infrastructure.Access;

public sealed record AudienceTerms(Guid Id, string Name, string? Aliases);
public sealed record PageSuggestion(bool Everyone, IReadOnlyList<Guid> AudienceIds, IReadOnlyList<string> Notes);
public sealed record SuggestionResult(IReadOnlyDictionary<Guid, PageSuggestion> Pages, IReadOnlyList<string> UnmatchedRoles);

/// <summary>
/// Proposes who should see each page from the "role / permission" facts that were already extracted from the document (no new AI call, no
/// document text is interpreted as an instruction). It only matches the role words against the audience names and aliases, and reads the permission
/// wording as "has access" or "does not". The result is a suggestion: nothing is saved until an admin applies it.
/// </summary>
public static class AccessSuggester
{
    private static readonly Regex Negative = new(
        @"\b(no access|not (allowed|permitted|authori[sz]ed|available|visible|applicable)|cannot|can ?not|can't|denied|forbidden|hidden|no permission|none|not accessible|blocked)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EveryoneRole = new(
        @"^(all|any|every)\s+(users?|roles?|staff|employees|people)$|^everyone$|^all$|^anyone$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Normalize(string? s) =>
        Regex.Replace(Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " "), @"\s+", " ").Trim();

    private static IEnumerable<string> Variants(string term)
    {
        yield return term;
        if (term.Length > 3 && term.EndsWith('s')) yield return term[..^1];
        else yield return term + "s";
    }

    /// <summary>Audience words (name + aliases), normalised.</summary>
    private static List<string> Words(AudienceTerms a) =>
        new[] { a.Name }.Concat((a.Aliases ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(Normalize).Where(x => x.Length >= 2).Distinct().ToList();

    /// <summary>Which audiences does this role name stand for? An audience whose name is exactly the role wins; otherwise any audience whose word appears in it.</summary>
    public static List<AudienceTerms> Match(string role, IReadOnlyList<AudienceTerms> audiences)
    {
        var r = Normalize(role);
        if (r.Length == 0) return new();
        var exact = audiences.Where(a => Words(a).Any(w => Variants(w).Contains(r))).ToList();
        if (exact.Count > 0) return exact;
        return audiences.Where(a => Words(a).Any(w => Variants(w).Any(v => Regex.IsMatch(r, $@"(^|\s){Regex.Escape(v)}($|\s)")))).ToList();
    }

    public static SuggestionResult Suggest(IEnumerable<(Guid PageId, string Role, string Permission)> roleFacts, IReadOnlyList<AudienceTerms> audiences)
    {
        var byPage = new Dictionary<Guid, (bool Everyone, HashSet<Guid> Ids, List<string> Notes)>();
        var unmatched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (pageId, role, permission) in roleFacts)
        {
            var roleName = Regex.Replace(role ?? "", @"\s+", " ").Trim();
            if (roleName.Length == 0) continue;
            var denied = Negative.IsMatch(permission ?? "");
            var note = $"{roleName}: {Clip(permission)}";

            if (!byPage.TryGetValue(pageId, out var entry)) byPage[pageId] = entry = (false, new HashSet<Guid>(), new List<string>());

            if (EveryoneRole.IsMatch(Normalize(roleName)))
            {
                if (!denied) { entry.Everyone = true; byPage[pageId] = entry; }
                entry.Notes.Add(note);
                continue;
            }

            var matched = Match(roleName, audiences);
            if (matched.Count == 0) { unmatched.Add(roleName); entry.Notes.Add(note); continue; }
            foreach (var a in matched) if (!denied) entry.Ids.Add(a.Id);
            entry.Notes.Add(note + (denied ? " (no access)" : ""));
            byPage[pageId] = entry;
        }

        // A role the document says is denied must not stay ticked just because another sentence for the same role grants something.
        var denials = roleFacts.Where(f => Negative.IsMatch(f.Permission ?? "")).GroupBy(f => f.PageId).ToDictionary(g => g.Key, g => g.SelectMany(f => Match(f.Role, audiences)).Select(a => a.Id).ToHashSet());
        var pages = byPage.ToDictionary(kv => kv.Key, kv =>
        {
            var ids = kv.Value.Ids.Where(i => !(denials.GetValueOrDefault(kv.Key)?.Contains(i) ?? false)).ToList();
            return new PageSuggestion(kv.Value.Everyone, ids, kv.Value.Notes.Take(4).ToList());
        });
        // Only pages where the document actually says something about a group we can place are worth suggesting.
        var useful = pages.Where(kv => kv.Value.Everyone || kv.Value.AudienceIds.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        return new SuggestionResult(useful, unmatched.Where(u => !EveryoneRole.IsMatch(Normalize(u))).ToList());
    }

    private static string Clip(string? s)
    {
        var t = Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return t.Length <= 90 ? t : t[..90].TrimEnd() + "…";
    }
}
