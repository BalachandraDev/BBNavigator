using System.Text.RegularExpressions;

namespace FlowMind.Infrastructure.Chat;

/// <summary>
/// Everyday talk that is not a documentation question: greetings, thanks, goodbyes, "who are you". Answered locally with a
/// friendly fixed reply, so it costs no AI call and never shows up as "not documented". Understood in English and Tamil; the reply
/// is in the language the visitor SELECTED (or, when none was given, the language they wrote in).
/// </summary>
public static class SmallTalk
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private enum Kind { None, Greeting, HowAreYou, Who, Thanks, Bye, Ack }

    // ---- English ----
    private static readonly Regex Greet = new(@"^(?:hi+|hello+|hey+|heya|hiya|yo|hola|namaste|greetings|good\s+(?:morning|afternoon|evening|day)|howdy|sup)\b[\s,!.]*", O);
    private static readonly Regex HowAreYou = new(@"^(?:how\s+are\s+(?:you|u)(?:\s+doing)?|how\s+r\s+u|how'?s\s+it\s+going|how\s+is\s+it\s+going|what'?s\s+up|whats\s+up|hope\s+you'?re\s+(?:well|good))$", O);
    private static readonly Regex Thanks = new(@"^(?:(?:ok(?:ay)?|great|cool|nice|awesome|perfect)[\s,]+)?(?:thanks?(?:\s+a\s+lot|\s+so\s+much|\s+again)?|thank\s+you(?:\s+so\s+much|\s+very\s+much)?|thx|ty|cheers|much\s+appreciated|appreciate\s+it)(?:\s+(?:you|bot|assistant|buddy))?$", O);
    private static readonly Regex Bye = new(@"^(?:bye+|goodbye|good\s*night|see\s+(?:you|ya)(?:\s+later)?|cya|take\s+care|talk\s+later|that'?s\s+all|i'?m\s+done)$", O);
    private static readonly Regex Ack = new(@"^(?:ok(?:ay)?|k|cool|great|nice|awesome|perfect|got\s+it|alright|fine|sure|understood|makes\s+sense|i\s+see|good|wow|yes|yep|yeah)$", O);
    private static readonly Regex Who = new(@"^(?:who\s+are\s+(?:you|u)|what\s+are\s+(?:you|u)|what'?s\s+your\s+name|what\s+is\s+your\s+name|your\s+name|introduce\s+yourself|what\s+can\s+you\s+do|what\s+do\s+you\s+do|what\s+can\s+i\s+ask(?:\s+you)?|can\s+you\s+help(?:\s+me)?|help(?:\s+me)?|i\s+need\s+help|how\s+do\s+(?:i|you)\s+use\s+this|how\s+does\s+this\s+work)$", O);

    // ---- Tamil (whole-message patterns; no \b because Tamil vowel signs are not "word" characters in .NET) ----
    private static readonly Regex TaGreet = new(@"^(?:வணக்கம்(?:\s*(?:ங்க|ங்கள்|பா|சார்|மேடம்))?|வணக்கமுங்க|ஹலோ|ஹாய்|ஹே|நமஸ்காரம்|நமஸ்தே|(?:காலை|மாலை|இரவு|மதிய)\s+வணக்கம்|இனிய\s+(?:காலை|மாலை|இரவு)|குட்\s+(?:மார்னிங்|ஆஃப்டர்னூன்|ஈவினிங்)|vanakkam|vanakam)[\s,!.]*$", O);
    private static readonly Regex TaHow = new(@"^(?:எப்படி\s+இருக்(?:கிறீர்கள்|கீங்க|கிறீங்க|கிறாய்|க|கிறேன்)|நலமா|சுகமா|நலம்தானா|என்ன\s+விசேஷம்|என்ன\s+செய்தி)$", O);
    private static readonly Regex TaThanks = new(@"^(?:(?:சரி|சூப்பர்|அருமை)\s+)?(?:(?:மிக்க\s+|ரொம்ப\s+)?நன்றி(?:கள்|ங்க|ங்கள்)?|தேங்க்ஸ்|தாங்க்ஸ்|nandri|thanks?)(?:\s+(?:சார்|மேடம்|பா))?$", O);
    private static readonly Regex TaBye = new(@"^(?:பை|பை\s*பை|டாடா|குட்\s+பை|குட்\s+நைட்|போய்\s+வருகிறேன்|சென்று\s+வருகிறேன்|பிறகு\s+பார்க்கலாம்|மீண்டும்\s+சந்திப்போம்|இரவு\s+வணக்கம்|அவ்வளவுதான்|போதும்)$", O);
    private static readonly Regex TaAck = new(@"^(?:சரி(?:ங்க|ங்கள்)?|ஓகே|ஓக்கே|நல்லது|புரிந்தது|புரிந்தது\s+நன்றி|அருமை|சூப்பர்|ஆமாம்|ஆம்|சரிதான்|நன்று)$", O);
    private static readonly Regex TaWho = new(@"^(?:நீ|நீங்கள்|நீங்க)\s+யார்$|^(?:உன்|உங்கள்|உங்க)\s+பெயர்\s+என்ன$|^(?:உதவி|உதவுங்கள்|உதவி\s+வேண்டும்|எனக்கு\s+உதவி\s+வேண்டும்|நீங்கள்\s+என்ன\s+செய்வீர்கள்|என்ன\s+செய்ய\s+முடியும்|இதை\s+எப்படி\s+பயன்படுத்துவது)$", O);

    /// <param name="language">The language the visitor selected ("en" or "ta"). Null = reply in the language the message is written in.</param>
    /// <returns>Null when the message is a real question that should go through the documents.</returns>
    public static string? TryReply(string message, string assistantName, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 80) return null;
        // Keep letters, combining marks (Tamil vowel signs), digits and apostrophes; everything else becomes a space.
        var m = Regex.Replace(message.Trim().ToLowerInvariant().Replace('’', '\''), @"[^\p{L}\p{M}\p{N}'\s]", " ");
        m = Regex.Replace(m, @"\s+", " ").Trim();
        if (m.Length == 0 || m.Split(' ').Length > 8) return null;

        // What was said is understood in either language; how it is answered follows the selected language.
        var kind = Classify(m);
        if (kind == Kind.None) return null;
        var written = Language.Detect(message) == Language.Tamil || TaGreet.IsMatch(m) && Regex.IsMatch(m, "^van?akk?am") || m == "nandri" ? Language.Tamil : Language.English;
        return Render(kind, language is Language.Tamil or Language.English ? language : written, assistantName);
    }

    private static Kind Classify(string m)
    {
        if (TaGreet.IsMatch(m)) return Kind.Greeting;
        if (TaHow.IsMatch(m)) return Kind.HowAreYou;
        if (TaWho.IsMatch(m)) return Kind.Who;
        if (TaThanks.IsMatch(m)) return Kind.Thanks;
        if (TaBye.IsMatch(m)) return Kind.Bye;
        if (TaAck.IsMatch(m)) return Kind.Ack;

        var greeted = false;
        var g = Greet.Match(m);
        if (g.Success) { greeted = true; m = m[g.Length..].Trim(); m = Regex.Replace(m, @"^(?:there|team|all|everyone|bot|assistant|buddy|friend)\b\s*", "", O).Trim(); }
        if (m.Length == 0) return greeted ? Kind.Greeting : Kind.None;
        if (HowAreYou.IsMatch(m)) return Kind.HowAreYou;
        if (Who.IsMatch(m)) return Kind.Who;
        if (Thanks.IsMatch(m)) return Kind.Thanks;
        if (Bye.IsMatch(m)) return Kind.Bye;
        if (!greeted && Ack.IsMatch(m)) return Kind.Ack;
        return Kind.None;
    }

    private static string Render(Kind kind, string language, string name)
    {
        if (language == Language.Tamil)
        {
            var intro = $"நான் {name}. இதைப் பற்றி எதையும் கேளுங்கள். உதாரணமாக, ஒரு திரை எப்படி செயல்படுகிறது, ஒரு விதி அல்லது சரிபார்ப்பு என்ன, ஒரு செயல்முறையில் அடுத்த படி என்ன என்று கேட்கலாம்.";
            return kind switch
            {
                Kind.Greeting => $"வணக்கம்! 👋 {intro}",
                Kind.HowAreYou => $"நன்றாக இருக்கிறேன், கேட்டதற்கு நன்றி! {intro}",
                Kind.Who => $"வணக்கம்! {intro}",
                Kind.Thanks => "மகிழ்ச்சி! வேறு கேள்வி இருந்தால் தயங்காமல் கேளுங்கள்.",
                Kind.Bye => "நன்றி, மீண்டும் சந்திப்போம்! கேள்வி இருந்தால் எப்போது வேண்டுமானாலும் வாருங்கள்.",
                _ => "மகிழ்ச்சி! உங்களுக்கு வேறு கேள்வி இருந்தால் கேளுங்கள்.",
            };
        }
        var intro1 = $"I'm {name}. Ask me anything about it, for example how a screen works, what a rule or validation is, or what the next step in a process is.";
        return kind switch
        {
            Kind.Greeting => $"Hello! 👋 {intro1}",
            Kind.HowAreYou => $"I'm doing well, thank you for asking! {intro1}",
            Kind.Who => $"Hi! {intro1}",
            Kind.Thanks => "You're welcome! If you have another question, just ask.",
            Kind.Bye => "Goodbye! Come back any time you have a question.",
            _ => "Glad to help! Ask me another question whenever you like.",
        };
    }

    /// <summary>Reply for a message that matched nothing in the documents (unrelated, or not covered), in the reply language.</summary>
    public static string OffTopic(string assistantName, string language = Language.English) => language == Language.Tamil
        ? $"நான் {assistantName} தொடர்பான கேள்விகளுக்கு மட்டுமே உதவ முடியும்; இதைப் பற்றி எதுவும் கிடைக்கவில்லை. ஒரு திரை, செயல்முறையின் ஒரு படி, ஒரு பங்கு, ஒரு விதி அல்லது ஒரு சரிபார்ப்பு பற்றிக் கேட்டுப் பாருங்கள்."
        : $"I can only help with questions about {assistantName}, and I couldn't find anything about that. " +
          "Try asking about a screen, a step in a process, a role, a rule or a validation, or type \"what can I ask?\" to see the topics available to you.";

    /// <summary>Reply for a question that is plainly not about the application (weather, jokes, sports, general knowledge): says what can be asked instead.</summary>
    /// <param name="modules">The modules this caller may ask about. Named in the reply ("I only answer questions about the BMW module"); file names are never shown.</param>
    public static string Unrelated(string assistantName, string language = Language.English, IReadOnlyList<string>? modules = null)
    {
        static string Join(IReadOnlyList<string> items, int max, string more, string and) =>
            items.Count == 1 ? items[0]
            : items.Count <= max ? string.Join(", ", items.Take(items.Count - 1)) + " " + and + " " + items[^1]
            : string.Join(", ", items.Take(max)) + more.Replace("{n}", (items.Count - max).ToString());
        var has = modules is { Count: > 0 };
        if (language == Language.Tamil)
        {
            var scope = has ? $"{Join(modules!, 5, " மற்றும் {n} மேலும்", "மற்றும்")} {(modules!.Count == 1 ? "தொகுதி" : "தொகுதிகள்")}" : assistantName;
            return $"இது என் வேலைக்கு அப்பாற்பட்டது. நான் {scope} தொடர்பான கேள்விகளுக்கு மட்டுமே பதில் தருவேன். " +
                   "எதைக் கேட்கலாம் என்று பார்க்க \"what can I ask?\" என்று தட்டச்சு செய்யுங்கள்.";
        }
        var en = "That's outside what I can help with. ";
        en += has
            ? $"I only answer relevant questions about the {Join(modules!, 5, ", and {n} more", "and")} {(modules!.Count == 1 ? "module" : "modules")}. "
            : $"I only answer questions about {assistantName}. ";
        return en + "Type \"what can I ask?\" to see everything you can ask about.";
    }

    /// <summary>The fixed "not documented" reply, in the reply language.</summary>
    public static string NotDocumented(string language) => language == Language.Tamil
        ? "இது உங்களுக்குக் கிடைக்கும் தலைப்புகளில் இல்லை. கேள்வியை வேறு விதமாகக் கேளுங்கள், அல்லது எதைக் கேட்கலாம் என்று பார்க்க \"what can I ask?\" என்று தட்டச்சு செய்யுங்கள்."
        : ChatService.NotDocumentedAnswer;
}
