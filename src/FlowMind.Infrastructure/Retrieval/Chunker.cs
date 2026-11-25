using System.Security.Cryptography;
using System.Text;

namespace FlowMind.Infrastructure.Retrieval;

public sealed record TextChunk(int Index, int PageNumber, string Content, string Hash);

/// <summary>
/// Splits extracted page text into overlapping chunks. A chunk never spans pages, so every chunk has one exact
/// page number for citations. Pure and deterministic: same input → same chunks and hashes.
/// </summary>
public static class Chunker
{
    private const int MinUsefulChars = 20;

    public static List<TextChunk> Build(IEnumerable<(int PageNumber, string Text)> pages, int maxChars, int overlap)
    {
        maxChars = Math.Max(200, maxChars);
        overlap = Math.Clamp(overlap, 0, maxChars / 3);
        var result = new List<TextChunk>();
        int index = 0;

        foreach (var (page, text) in pages.OrderBy(p => p.PageNumber))
        {
            foreach (var content in SplitPage(text, maxChars, overlap))
            {
                if (content.Count(c => !char.IsWhiteSpace(c)) < MinUsefulChars) continue;
                result.Add(new TextChunk(index++, page, content, Hash(content)));
            }
        }
        return result;
    }

    internal static IEnumerable<string> SplitPage(string text, int maxChars, int overlap)
    {
        var units = new List<string>();
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            while (line.Length > maxChars)
            {
                var cut = line.LastIndexOf(' ', maxChars - 1);
                if (cut < maxChars / 2) cut = maxChars;
                units.Add(line[..cut].TrimEnd());
                line = line[cut..].TrimStart();
            }
            if (line.Length > 0) units.Add(line);
        }

        var sb = new StringBuilder();
        foreach (var u in units)
        {
            if (sb.Length > 0 && sb.Length + 1 + u.Length > maxChars)
            {
                var done = sb.ToString();
                yield return done;
                sb.Clear();
                var tail = Tail(done, overlap);
                if (tail.Length > 0) sb.Append(tail);
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(u);
        }
        if (sb.Length > 0) yield return sb.ToString(); // always contains at least one unit not yet emitted
    }

    private static string Tail(string s, int overlap)
    {
        if (overlap <= 0 || s.Length <= overlap) return "";
        var start = s.Length - overlap;
        var space = s.IndexOfAny(new[] { ' ', '\n' }, start);
        return space < 0 ? "" : s[(space + 1)..];
    }

    public static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
