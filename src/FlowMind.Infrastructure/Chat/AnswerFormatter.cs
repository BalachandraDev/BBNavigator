using System.Text.RegularExpressions;

namespace FlowMind.Infrastructure.Chat;

/// <summary>
/// Turns a grounded model answer into clean reading text. Citation numbers are used internally (to check the answer is
/// supported and to build the Sources list) but are never shown inside the answer: no "[2]", no "*Source: 2*" lines, no
/// "(Source 1)". Only the notation is removed; the sentences and their Markdown structure are left exactly as written.
/// </summary>
public static class AnswerFormatter
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // A whole line that only labels a source: "*Source: [2]*", "Sources: 1, 3", "> References: ...", "— Source 2".
    // Requires the label to be the whole word followed by ":" (and anything) or only numbers/brackets, so "Sourcing rules apply..." is never touched.
    private static readonly Regex SourceLine = new(@"^[ \t>*_\-–—•]*\**_*(?:sources?|references?)\b(?:\s*[:：].*|[\s\d,;&\[\]()*_]*)$", Ci | RegexOptions.Multiline | RegexOptions.Compiled);
    // Inline: "(Source 2)", "(Sources: 1, 2)", "[Source 2]", "[1]", "[1][2]", "【1】", also "(see source 3)".
    private static readonly Regex InlineLabel = new(@"[\(\[（]\s*(?:see\s+)?(?:sources?|refs?\.?|references?)\s*:?\s*\d{1,2}(?:\s*[,;&and]+\s*\d{1,2})*\s*[\)\]）]", Ci | RegexOptions.Compiled);
    // What is left of an inline "*Source: [2]*" once the numbers are removed: a label with nothing after it, at the end of a line.
    private static readonly Regex EmptyLabel = new(@"[ \t]*[*_]{0,2}(?:sources?|references?)[ \t]*:[ \t]*(?:\d{1,2}(?:[ \t]*[,;&][ \t]*\d{1,2})*)?[ \t]*[*_]{0,2}[ \t]*(?=\n|$)", Ci | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Marker = new(@"\s*[\[【]\s*\d{1,2}(?:\s*[,;]\s*\d{1,2})*\s*(?:†[^\]】]*)?[\]】]", RegexOptions.Compiled);

    // "according to the sources," / "listed in the BMW module documentation" / "as mentioned in the document": how the knowledge is stored is never shown.
    // Only phrases that point AT the storage (a cue word + "the ... documentation/documents/sources/PDF/file") are touched, so a real feature such as
    // "upload the patient documents" is left alone.
    private static readonly Regex StorageCue = new(
        @"\b(?<cue>according to|based on|as per|(?:as )?(?:stated|mentioned|described|listed|shown|given|found|provided|explained|covered|specified) in)\s+the\s+(?:(?:provided|approved|available|attached|uploaded|given)\s+)?(?<inner>(?:[\w-]+\s+){0,3}?)(?:documentation|documents?|sources?|pdfs?|files?|excerpts?)\b(?<tail>[,:]?\s*)",
        Ci | RegexOptions.Compiled);
    // "..., each with a source citation"
    private static readonly Regex WithCitation = new(@",?\s*(?:each\s+)?with\s+(?:a\s+|an\s+|its\s+|their\s+)?(?:source\s+)?(?:citations?|references?)\b", Ci | RegexOptions.Compiled);

    private static string HideStorage(string s)
    {
        s = WithCitation.Replace(s, "");
        s = StorageCue.Replace(s, m =>
        {
            var cue = m.Groups["cue"].Value; var inner = m.Groups["inner"].Value.Trim();
            var leading = Regex.IsMatch(cue, "^(?:according to|based on|as per)$", Ci);
            if (inner.Length > 0) return $"{cue} the {inner}{m.Groups["tail"].Value}";
            if (leading) return "\u0001";                                  // "According to the sources, X" -> "X"
            return $"{cue} the system{m.Groups["tail"].Value}";
        });
        s = Regex.Replace(s, "\u0001([a-z])", m => m.Groups[1].Value.ToUpperInvariant());
        return s.Replace("\u0001", "");
    }

    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Replace("\r\n", "\n");
        s = HideStorage(s);

        s = SourceLine.Replace(s, "");
        s = InlineLabel.Replace(s, "");
        s = Marker.Replace(s, "");
        s = EmptyLabel.Replace(s, "");

        // Tidy what removal leaves behind: space before punctuation, runs of blank lines, trailing spaces.
        s = Regex.Replace(s, @"[ \t]+([.,;:!?])", "$1");
        s = Regex.Replace(s, @"[ \t]+\n", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        s = Regex.Replace(s, @"(?m)^[ \t]*[*_]{1,2}[ \t]*$", "");      // a leftover empty "**" or "*" line
        return s.Trim();
    }
}
