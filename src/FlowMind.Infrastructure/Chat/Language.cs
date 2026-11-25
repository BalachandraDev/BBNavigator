namespace FlowMind.Infrastructure.Chat;

/// <summary>
/// Which language a person is writing in. Local and instant (no AI call): Tamil is recognised by its own script, everything else is
/// treated as English. A Tamil sentence with English product or page names in it ("Dispensing பக்கத்தில் எடைகளை எப்படி உள்ளிடுவது?") is Tamil.
/// The answer is written in the language of the question; document text may be in either language.
/// </summary>
public static class Language
{
    public const string English = "en";
    public const string Tamil = "ta";

    public static bool IsTamilChar(char c) => c >= '஀' && c <= '௿';

    public static bool HasTamil(string? text) => !string.IsNullOrEmpty(text) && text.Any(IsTamilChar);

    /// <summary>"ta" when a quarter or more of the letters are Tamil script, otherwise "en".</summary>
    public static string Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return English;
        int tamil = 0, letters = 0;
        foreach (var c in text)
        {
            if (IsTamilChar(c)) { tamil++; letters++; }
            else if (char.IsLetter(c)) letters++;
        }
        return tamil >= 2 && tamil * 4 >= letters ? Tamil : English;
    }

    /// <summary>How the model is told which language to reply in.</summary>
    public static string Label(string language) => language == Tamil ? "Tamil (தமிழ்), written in Tamil script" : "English";
}
