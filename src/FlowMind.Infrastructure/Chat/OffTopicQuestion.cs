using System.Text.RegularExpressions;

namespace FlowMind.Infrastructure.Chat;

/// <summary>
/// Questions that are plainly not about the application: weather, jokes, sports scores, news, prices, celebrities, general knowledge,
/// arithmetic, "write me a poem", "which AI are you". Recognised locally (no embedding, no search, no AI call) and answered with a short,
/// relevant message that says what the assistant can help with. The patterns are deliberately narrow: anything that might be a real
/// question about the documents (a field called "temperature", a "score" on a form) is left to the normal search.
/// </summary>
public static class OffTopicQuestion
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex[] Patterns =
    {
        // weather / forecast
        new(@"\b(?:weather|forecast|will it rain|is it raining)\b", O),
        // entertainment on request
        new(@"\b(?:tell|say|give|crack)\b.{0,20}\b(?:joke|riddle|story|poem|fun fact)\b", O),
        new(@"^(?:please )?(?:sing|write|compose|draft)\b.{0,15}\b(?:song|poem|story|essay|joke|rap|lyrics|haiku|letter|email|e mail|speech|code|program|script)\b", O),
        new(@"\b(?:lyrics|horoscope|astrology|zodiac|recipe for|how to cook|movie review|best movies?|netflix|youtube|instagram|facebook|whatsapp status)\b", O),
        new(@"\b(?:suggest|recommend|recommendation|name)\b.{0,25}\b(?:songs?|music|movies?|films?|books?|series|restaurants?|games?|gifts?|names? for my)\b", O),
        new(@"^(?:play|sing|play me)\b.{0,25}\b(?:song|music)\b", O),
        // sport, news, money markets
        new(@"\b(?:cricket|football|ipl|world cup|premier league|nba|tennis|match score|live score|who won)\b", O),
        new(@"\b(?:latest news|today'?s news|breaking news|headlines|election results?|stock price|share price|sensex|nifty|bitcoin|crypto|gold rate|petrol price|exchange rate|dollar rate)\b", O),
        // general knowledge about the world
        new(@"^(?:what|who) (?:is|are|was) (?:the )?(?:capital of|population of|president of|prime minister|chief minister|richest|tallest)\b", O),
        new(@"^(?:where|when|who) (?:is|was|were) (?:the )?(?:eiffel|taj mahal|great wall|moon landing|einstein|gandhi|modi|trump|elon|musk|messi|dhoni|kohli|rajini|vijay|shah rukh|salman)\b", O),
        new(@"^(?:translate|convert)\b.+\b(?:into|to|in)\b (?:english|tamil|hindi|french|spanish|german|telugu|malayalam|kannada)\b", O),
        // coding and maths help
        new(@"\b(?:write|give|show|generate)\b.{0,25}\b(?:python|javascript|java|c#|c\+\+|sql|html|css|react)\b.{0,25}\b(?:code|program|script|function|query|snippet)\b", O),
        new(@"^(?:what is |what'?s |calculate |solve |compute )?[\d\s.,]+(?:[+\-*/x×÷^%]\s*[\d\s.,]+)+=?\s*\??$", O),
        // about the AI itself (not the application)
        new(@"\b(?:are you|r u)\b.{0,12}\b(?:chat ?gpt|gpt|openai|claude|gemini|llama|google|siri|alexa|a robot|human|real person)\b", O),
        new(@"\b(?:which|what) (?:ai|llm|model|language model)\b.{0,15}\b(?:are you|do you use|is this|powers)\b", O),
        // Tamil
        new(@"(?:வானிலை|மழை பெய்யுமா|நகைச்சுவை சொல்|ஜோக் சொல்|கிரிக்கெட்|சினிமா|திரைப்படம் பற்றி|பாடல் வரிகள்|சமையல் குறிப்பு|ராசிபலன்|தங்கம் விலை|பெட்ரோல் விலை)", O),
    };

    public static bool IsMatch(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 160) return false;
        var m = Regex.Replace(message.Trim().ToLowerInvariant().Replace('’', '\''), @"[^\p{L}\p{M}\p{N}'\s+\-*/x×÷^%=.,]", " ");
        m = Regex.Replace(m, @"\s+", " ").Trim();
        if (m.Length == 0) return false;
        foreach (var p in Patterns) if (p.IsMatch(m)) return true;
        return false;
    }
}
