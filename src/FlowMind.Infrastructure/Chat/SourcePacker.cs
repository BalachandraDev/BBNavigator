using System.Text.RegularExpressions;
using FlowMind.Application.Interfaces;

namespace FlowMind.Infrastructure.Chat;

public sealed record PackedSource(ChunkHit Hit, string Text);

/// <summary>
/// Decides which retrieved excerpts the model reads. Excerpts are kept WHOLE, best-ranked first, until a character budget
/// is used up; only the last one may be cut, and only at the end of a line. Lines repeated by chunk overlap are sent once.
///
/// Lines are never picked out of the middle of an excerpt. An earlier version kept only the lines that shared words with
/// the question; on real documents that deleted the table rows that say what comes NEXT (they do not repeat the question's
/// words) and made "what do I do after X?" unanswerable. Tokens are saved by the budget, not by guessing relevance.
/// </summary>
public static class SourcePacker
{
    private const int MinUsefulChars = 40;
    /// <summary>A partly-fitting last excerpt is only worth including when a reasonable part of it fits.</summary>
    private const int MinPartialChars = 400;

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","for","are","but","not","you","all","can","her","was","one","our","out","has","how","its","who","did","get","may","new","now","see","way","what","when","where","which","with","this","that","from","have","does","about","into","than","then","them","they","will","would","could","should","there","their","these","those","been","being","also","some","such","only","each","other","page","please","tell","show","explain","give","list",
    };

    /// <summary>Content words of a question (used to match workflows, not to delete text).</summary>
    public static HashSet<string> Terms(string query) =>
        Regex.Matches(query ?? "", @"[\p{L}\p{N}]{3,}").Select(m => m.Value.ToLowerInvariant()).Where(t => !Stop.Contains(t)).ToHashSet();

    public static List<PackedSource> Pack(IReadOnlyList<ChunkHit> hits, int totalChars, int eachChars)
    {
        var seen = new HashSet<string>();
        var result = new List<PackedSource>();
        var used = 0;

        foreach (var hit in hits)   // already ranked best-first
        {
            var room = totalChars - used;
            if (room < MinUsefulChars) break;

            // Lines already included from a neighbouring chunk (chunk overlap) add tokens but no information.
            var lines = hit.Content.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
                .Where(l => l.Length < 20 || !seen.Contains(Key(l))).ToList();
            if (lines.Count == 0) continue;

            var whole = string.Join("\n", lines);
            var cap = Math.Min(eachChars, room);
            if (whole.Length > cap && cap < MinPartialChars && result.Count > 0) break;   // only a sliver fits: skip rather than send a fragment

            var text = whole.Length <= cap ? whole : CutAtLine(lines, cap);
            if (text.Length < 15 && result.Count > 0) continue;

            foreach (var l in text.Split('\n')) seen.Add(Key(l));
            result.Add(new PackedSource(hit, text));
            used += text.Length;
        }
        return result;
    }

    /// <summary>Keeps the first lines that fit, in order. A single over-long line is cut at a word boundary.</summary>
    internal static string CutAtLine(IReadOnlyList<string> lines, int max)
    {
        var kept = new List<string>();
        var used = 0;
        foreach (var l in lines)
        {
            var len = l.Length + (kept.Count > 0 ? 1 : 0);
            if (used + len > max) break;
            kept.Add(l); used += len;
        }
        if (kept.Count > 0) return string.Join("\n", kept);
        var first = lines[0];
        var cut = first.LastIndexOf(' ', Math.Min(max, first.Length) - 1);
        return first[..(cut > max / 2 ? cut : Math.Min(max, first.Length))].TrimEnd() + "…";
    }

    private static string Key(string line) => Regex.Replace(line.ToLowerInvariant(), @"\s+", " ");
}
