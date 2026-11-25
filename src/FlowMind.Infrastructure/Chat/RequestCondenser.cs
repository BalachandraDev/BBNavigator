using System.Text.RegularExpressions;

namespace FlowMind.Infrastructure.Chat;

/// <param name="Text">What is actually sent on to the retrieval step and the model.</param>
/// <param name="Shortened">True when the message was cut down enough that the user should be told what was understood.</param>
public sealed record CondensedMessage(string Text, bool Shortened);

/// <summary>
/// The "request layer": turns whatever a user typed (greetings, thanks, e-mail signatures, quoted replies, pasted logs,
/// the same sentence three times) into a short, proper question before anything is sent to an AI provider.
///
/// It is deliberately LOCAL and deterministic — no AI call, so it costs no tokens and no quota itself. It only ever
/// REMOVES material (fluff, repetition, bulk) and keeps the user's own sentences verbatim; it never rewrites meaning:
/// negations, numbers, identifiers and quoted text are untouched. Applying it twice gives the same result.
/// </summary>
public static class RequestCondenser
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex Invisible = new(@"[​-‍⁠﻿]", RegexOptions.Compiled);
    private static readonly Regex Control = new(@"[\p{C}-[\r\n\t]]", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t ]+", RegexOptions.Compiled);
    private static readonly Regex SentenceSplit = new(@"(?<=[.!?])\s+|\r?\n+", RegexOptions.Compiled);

    private static readonly Regex Quoted = new(@"^\s*>", RegexOptions.Compiled);
    private static readonly Regex Wrote = new(@"^\s*(on .{5,90} wrote|from:|sent:|to:|cc:|subject:)\b.*$", Ci | RegexOptions.Compiled);
    private static readonly Regex ClosingWord = new(@"^\s*(thanks?|thank you|many thanks|regards|best regards|kind regards|warm regards|best wishes|cheers|sincerely|yours (sincerely|faithfully)|sent from my\b.*|-{2,})", Ci | RegexOptions.Compiled);
    private static readonly Regex GreetingOnly = new(@"^\s*(hi|hello|hey|dear|good (morning|afternoon|evening))\b[\w ,.!-]{0,40}$", Ci | RegexOptions.Compiled);
    private static readonly Regex GreetingLead = new(@"^\s*(hi|hello|hey|dear)\b[^,.!?:\n]{0,30}[,!:.\-]\s*", Ci | RegexOptions.Compiled);

    // Leading filler. Each rule keeps the user's verb and the question word, so the meaning is unchanged.
    private static readonly Regex[] Leads =
    {
        new(@"^(so|ok(ay)?|well|actually|basically|anyway)[,\s]+", Ci | RegexOptions.Compiled),
        new(@"^(could|would) you (please |kindly )?", Ci | RegexOptions.Compiled),
        new(@"^(can|will) you (please|kindly) ", Ci | RegexOptions.Compiled),
        new(@"^i(?:'d| would) (?:like|love) to (?:know|understand|ask)\s*(?:about|regarding)?\s*[,:]?\s*", Ci | RegexOptions.Compiled),
        new(@"^i (?:want|need|wish) to (?:know|understand)\s*(?:about|regarding)?\s*[,:]?\s*", Ci | RegexOptions.Compiled),
        new(@"^i(?: was|'m| am) (?:wondering|curious)\s*(?:about|regarding)?\s*[,:]?\s*", Ci | RegexOptions.Compiled),
        new(@"^i have a (?:quick )?question[,:.]?\s*(?:about|regarding)?\s*", Ci | RegexOptions.Compiled),
        new(@"^(?:tell me|let me know|show me)\s+(?=(?:what|which|who|when|where|why|how|if|whether)\b)", Ci | RegexOptions.Compiled),
    };
    private static readonly Regex Politeness = new(@"\s*\b(?:please|kindly)\b\s*,?", Ci | RegexOptions.Compiled);
    private static readonly Regex QuestionStart = new(
        @"^(what|which|who|whom|whose|when|where|why|how|is|are|does|do|did|can|could|should|will|would|explain|list|describe|show|give|tell|walk|summari[sz]e|compare|define|find)\b", Ci | RegexOptions.Compiled);
    private static readonly Regex Identifier = new(@"\d|""[^""]+""|'[^']+'|\b[A-Z]{2,}\b|\b[a-z]+[A-Z]\w*\b|_", RegexOptions.Compiled);

    private const int MaxSentenceChars = 300;
    private const int ContextSentenceChars = 160;
    private const int SignatureLines = 3;

    public static CondensedMessage Condense(string? message, int maxQuestionChars = 400)
    {
        var original = Normalize(message);
        if (original.Length == 0) return new CondensedMessage("", false);

        var sentences = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var signatureLeft = 0;       // after "Thanks," / "Best regards," the next few short lines are a name/title, not content
        foreach (var rawLine in (message ?? "").Replace("\r", "").Split('\n'))
        {
            var line = Normalize(rawLine);
            if (line.Length == 0 || Quoted.IsMatch(rawLine) || Wrote.IsMatch(line) || GreetingOnly.IsMatch(line)) continue;
            if (IsClosing(line)) { signatureLeft = SignatureLines; continue; }
            if (signatureLeft > 0 && line.Length <= 40 && !line.Contains('?')) { signatureLeft--; continue; }
            signatureLeft = 0;

            foreach (var piece in SentenceSplit.Split(line))
            {
                if (IsClosing(piece.Trim())) continue;   // "Thanks in advance!" after the question on the same line
                var s = Strip(piece);
                if (s.Length < 2 || !seen.Add(Key(s))) continue;
                sentences.Add(s);
            }
        }
        if (sentences.Count == 0) return new CondensedMessage(Cut(original, maxQuestionChars), original.Length > maxQuestionChars);

        var joined = string.Join(" ", sentences);
        var text = joined.Length <= maxQuestionChars ? joined : Select(sentences, maxQuestionChars);
        if (text.Length == 0) text = Cut(original, maxQuestionChars);

        // Tell the user what was understood only when something substantial was dropped (not for trimmed pleasantries).
        var shortened = original.Length - text.Length >= 40 && text.Length < original.Length * 0.8;
        return new CondensedMessage(text, shortened);
    }

    private static string Strip(string sentence)
    {
        var start = sentence.Trim();
        var s = GreetingLead.Replace(start, "");
        for (var pass = 0; pass < 2; pass++)
            foreach (var rule in Leads) s = rule.Replace(s, "");
        s = Politeness.Replace(s, "").Trim().Trim(',', ';', ':').Trim();
        // Re-capitalise only where words were removed from the front ("could you explain x" -> "Explain x"); never touch the user's own casing.
        if (s.Length > 0 && char.IsLower(s[0]) && !start.StartsWith(s, StringComparison.Ordinal)) s = char.ToUpperInvariant(s[0]) + s[1..];
        return s;
    }

    private static bool IsClosing(string line) => ClosingWord.IsMatch(line) && !line.Contains('?') && line.Length <= 60;

    private static bool IsStrong(string s) => s.Contains('?') || QuestionStart.IsMatch(s);

    /// <summary>
    /// Keep what looks like the actual ask (questions and imperatives), in their original order, plus the short sentence
    /// right before each (a referent such as "that error" usually lives there). Background is dropped, not used to fill space.
    /// </summary>
    private static string Select(List<string> sentences, int budget)
    {
        var chosen = new List<int>();
        var used = 0;
        var strong = Enumerable.Range(0, sentences.Count).Where(i => IsStrong(sentences[i])).ToList();

        if (strong.Count > 0)
        {
            foreach (var i in strong)
            {
                var len = Clip(sentences[i]).Length + 1;
                if (used + len > budget && chosen.Count > 0) continue;
                chosen.Add(i); used += len;
            }
            foreach (var i in chosen.ToList())
            {
                var p = i - 1;
                if (p < 0 || chosen.Contains(p) || IsStrong(sentences[p]) || sentences[p].Length > ContextSentenceChars || used + sentences[p].Length + 1 > budget) continue;
                chosen.Add(p); used += sentences[p].Length + 1;
            }
        }
        else
        {
            // No question or instruction anywhere: fall back to the most specific sentences (identifiers, first, last).
            var ranked = sentences.Select((s, i) => (s, i, score: (Identifier.IsMatch(s) ? 1 : 0) + (i == 0 ? 2 : 0) + (i == sentences.Count - 1 ? 1 : 0) - (s.Length > MaxSentenceChars ? 2 : 0)))
                .OrderByDescending(x => x.score).ThenBy(x => x.i);
            foreach (var (s, i, _) in ranked)
            {
                var len = Clip(s).Length + 1;
                if (used + len > budget && chosen.Count > 0) continue;
                chosen.Add(i); used += len;
            }
        }
        var text = string.Join(" ", chosen.OrderBy(i => i).Select(i => Clip(sentences[i])));
        return text.Length > budget ? Cut(text, budget) : text;
    }

    /// <summary>An over-long "sentence" is usually pasted text. If it holds a question that question is at its end, so keep the tail.</summary>
    private static string Clip(string s)
    {
        if (s.Length <= MaxSentenceChars) return s;
        if (!s.Contains('?')) return Cut(s, MaxSentenceChars);
        var from = s.Length - MaxSentenceChars;
        var space = s.IndexOf(' ', from);
        return "…" + s[(space > 0 ? space + 1 : from)..];
    }

    private static string Cut(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = s.LastIndexOf(' ', max - 1);
        return s[..(cut > max / 2 ? cut : max)].TrimEnd(',', ';', ':', ' ') + "…";
    }

    private static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Control.Replace(Invisible.Replace(s, ""), " ");
        return Spaces.Replace(Regex.Replace(s, @"\s*\n\s*", " "), " ").Trim();
    }

    private static string Key(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{M}\p{N}]+", " ").Trim();
}
