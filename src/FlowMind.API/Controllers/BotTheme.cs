using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace FlowMind.API.Controllers;

/// <summary>
/// Everything a company admin can change about a share link's page. Every field is an allow-listed choice, a #rrggbb colour or a short
/// plain-text string, so the admin has wide control but can never inject CSS, HTML or script into the visitor's page.
/// </summary>
public sealed class BotTheme
{
    private const string Hex = "^(#[0-9a-fA-F]{6})?$";
    private const string HexMsg = "Colours must look like #3752d6.";

    /// <summary>Default light/dark for visitors who have not picked one themselves.</summary>
    [RegularExpression("^(auto|light|dark)$")] public string Mode { get; set; } = "auto";
    /// <summary>Page background style. "solid" uses Bg1 (or the default), "gradient" blends Bg1 into Bg2, the rest draw a pattern or glow on Bg1.</summary>
    [RegularExpression("^(solid|gradient|dots|grid|aurora)$")] public string Background { get; set; } = "solid";
    [RegularExpression(Hex, ErrorMessage = HexMsg)] public string? Bg1 { get; set; }
    [RegularExpression(Hex, ErrorMessage = HexMsg)] public string? Bg2 { get; set; }
    [RegularExpression("^(system|serif|rounded|mono)$")] public string Font { get; set; } = "system";
    [RegularExpression("^(sm|md|lg)$")] public string FontSize { get; set; } = "md";
    [RegularExpression("^(sharp|soft|round)$")] public string Radius { get; set; } = "round";
    /// <summary>Visitor message style.</summary>
    [RegularExpression("^(solid|soft|outline)$")] public string Bubble { get; set; } = "solid";
    [RegularExpression("^(narrow|standard|wide)$")] public string Width { get; set; } = "standard";
    [RegularExpression("^(plain|accent|glass)$")] public string Header { get; set; } = "plain";
    [RegularExpression("^(sparkle|chat|book|bolt|user|info)$")] public string Avatar { get; set; } = "sparkle";
    /// <summary>Optional emoji or initials shown instead of the avatar icon (plain text).</summary>
    [StringLength(4)] public string? AvatarText { get; set; }
    [StringLength(80)] public string? Placeholder { get; set; }
    [StringLength(200)] public string? Footer { get; set; }
    public bool ShowFooter { get; set; } = true;
    public bool ShowThemeToggle { get; set; } = true;
    public bool ShowStatus { get; set; } = true;
    public bool TypingEffect { get; set; } = true;
    public bool ShowCopy { get; set; } = true;
    /// <summary>When the assistant cannot answer, offer "Send this to our team": the question lands in the admin's support inbox.</summary>
    public bool ShowSupport { get; set; } = true;
    // ---- Tamil / English ----
    /// <summary>Language the page opens in until the visitor picks one: auto = the browser's language (Tamil if it is Tamil, otherwise English).</summary>
    [RegularExpression("^(auto|en|ta)$")] public string DefaultLanguage { get; set; } = "auto";
    /// <summary>Show the English / தமிழ் switch in the header.</summary>
    public bool ShowLanguageSwitch { get; set; } = true;
    /// <summary>Tamil versions of the texts the admin wrote. Empty = the English text is shown for Tamil visitors too.</summary>
    [StringLength(120)] public string? TitleTa { get; set; }
    [StringLength(500)] public string? WelcomeTa { get; set; }
    [MaxLength(4)] public List<string>? StartersTa { get; set; }
    [StringLength(80)] public string? PlaceholderTa { get; set; }
    [StringLength(200)] public string? FooterTa { get; set; }

    /// <summary>Visitors can see, reopen and delete their own earlier chats on this device.</summary>
    public bool HistoryEnabled { get; set; } = true;
    /// <summary>Chats older than this many days (since last message) are removed automatically.</summary>
    [Range(1, 365)] public int HistoryDays { get; set; } = 30;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Same rules the API applies on input, used again when reading stored values so a bad row can never reach a visitor.</summary>
    public bool IsValid() => Validator.TryValidateObject(this, new ValidationContext(this), null, true);

    public BotTheme Normalized()
    {
        Bg1 = string.IsNullOrWhiteSpace(Bg1) ? null : Bg1.ToLowerInvariant();
        Bg2 = string.IsNullOrWhiteSpace(Bg2) ? null : Bg2.ToLowerInvariant();
        AvatarText = string.IsNullOrWhiteSpace(AvatarText) ? null : AvatarText.Trim();
        Placeholder = string.IsNullOrWhiteSpace(Placeholder) ? null : Placeholder.Trim();
        Footer = string.IsNullOrWhiteSpace(Footer) ? null : Footer.Trim();
        TitleTa = string.IsNullOrWhiteSpace(TitleTa) ? null : TitleTa.Trim();
        WelcomeTa = string.IsNullOrWhiteSpace(WelcomeTa) ? null : WelcomeTa.Trim();
        PlaceholderTa = string.IsNullOrWhiteSpace(PlaceholderTa) ? null : PlaceholderTa.Trim();
        FooterTa = string.IsNullOrWhiteSpace(FooterTa) ? null : FooterTa.Trim();
        StartersTa = StartersTa?.Select(s => (s ?? "").Trim()).Where(s => s.Length > 0).Select(s => s.Length > 120 ? s[..120] : s).Take(4).ToList();
        if (StartersTa is { Count: 0 }) StartersTa = null;
        return this;
    }

    public static BotTheme Read(string? json)
    {
        try
        {
            var t = string.IsNullOrWhiteSpace(json) ? new BotTheme() : JsonSerializer.Deserialize<BotTheme>(json, Json) ?? new BotTheme();
            return t.IsValid() ? t : new BotTheme();
        }
        catch { return new BotTheme(); }
    }

    public string? Pack() => JsonSerializer.Serialize(Normalized(), Json);
}
