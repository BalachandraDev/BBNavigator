using System.Text.RegularExpressions;

namespace FlowMind.Infrastructure.Chat;

/// <summary>
/// "What can I ask?", "what data can be shown?", "what is approved for me to search?", "list the modules": questions about the assistant's own
/// coverage, not about the documents. They are recognised locally (short, whole-message patterns, so a real question such as
/// "what can I see on the dashboard page" still goes to the documents) and answered with the list of topics this caller may see.
/// </summary>
public static class ScopeQuestion
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex[] Patterns =
    {
        // "what can I ask / search / see / access / know (about) (here)"
        new(@"^(?:so |ok |okay |please |hey |hi )?what (?:can|could|may|should) (?:i|we) (?:ask|search|see|view|access|read|find|look up|know)(?: about| here| from you| for| in here| now)*$", O),
        // "what data can be shown", "what information is available for me", "what approved for me to search", "which topics do you have"
        new(@"^(?:so |ok |okay |please |hey |hi )?(?:what|which|tell me what|show me what)\b(?:(?!\b(?:on|in|at|inside|about) the\b).)*\b(?:approved|allowed|available|shown|covered|searchable)\b(?:(?!\b(?:on|in|at|inside|about) the\b).)*$", O),
        new(@"^(?:what|which)\b.*\b(?:data|information|info|topics?|modules?|pages?|documents?|content|things)\b.*\b(?:for me|to me|do you have|can i|i can|you have|you know|are there|is there|do i have)\b.*$", O),
        // "list the modules", "show me all topics available"
        new(@"^(?:please )?(?:list|show|give|tell)(?: me)?(?: all)?(?: of)?(?: the)?(?: available| approved)? ?(?:modules?|pages?|topics?|documents?|screens?|areas|data)(?: available| approved)?(?: for me| to me| i can see| i can ask)?$", O),
        // "what do you know", "what do you cover", "what is covered", "what topics"
        new(@"^(?:what (?:do|does) (?:you|this|it) (?:know|cover|contain|have)(?: about)?|what is covered|what topics(?: are there)?|what are the topics|what modules(?: are there)?)$", O),
    };

    // A question that mentions how to do something, a rule, a role or a step is about the documents, never about coverage.
    private static readonly Regex About = new(@"\b(?:how|why|register|create|add|edit|delete|remove|print|enter|cancel|submit|save|discharge|admit|validations?|rules?|roles?|steps?|buttons?|fields?|workflows?|permissions?|next|after|before|error)\b", O);

    public static bool IsMatch(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 100) return false;
        var m = Regex.Replace(message.Trim().ToLowerInvariant().Replace('’', '\''), @"[^\p{L}\p{N}'\s]", " ");
        m = Regex.Replace(m, @"\s+", " ").Trim();
        var words = m.Length == 0 ? 0 : m.Split(' ').Length;
        if (words is < 2 or > 11 || About.IsMatch(m)) return false;
        foreach (var p in Patterns) if (p.IsMatch(m)) return true;
        return false;
    }
}
