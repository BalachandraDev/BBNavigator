namespace FlowMind.Domain.Entities;

public class Conversation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    /// <summary>Token subject of the signed-in user who started it. Users only ever see their own conversations.</summary>
    public string OwnerId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A finished answer, reusable while the question and the document excerpts it was based on are unchanged.
/// The key is a hash over tenant + application + condensed question + the exact excerpt text, so editing or deleting a
/// document changes the key and the stale answer is simply never found again.
/// </summary>
public class ChatCacheEntry : ITenantOwned
{
    public string Key { get; set; } = "";
    public Guid TenantId { get; set; }
    public string Answer { get; set; } = "";
    public bool Grounded { get; set; }
    public string? CitationsJson { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A shareable link to a chat-only page for one application. Anyone holding the link can ask questions (no sign-in), so it is
/// revocable, rate-limited and capped per day. Only a hash of the token is used for lookup; an encrypted copy lets the company
/// admin copy the link again. Visitors never see document names, pages or anything else about the company.
/// </summary>
public class BotLink : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    /// <summary>Internal label for the admin ("Website widget", "HR team").</summary>
    public string Name { get; set; } = "";
    /// <summary>Shown to visitors as the assistant's name.</summary>
    public string Title { get; set; } = "";
    public string? Welcome { get; set; }
    /// <summary>JSON array of up to four starter questions.</summary>
    public string? StartersJson { get; set; }
    /// <summary>Accent colour of the chat page, "#rrggbb". Null = the default blue.</summary>
    public string? AccentColor { get; set; }
    /// <summary>JSON array of document ids this link may answer from (a module or a few documents). Null = every approved document of the application.</summary>
    public string? DocumentIdsJson { get; set; }
    /// <summary>Audience whose access rules limit this link. Null = no audience limit (the link answers from everything it is mapped to).</summary>
    public Guid? AudienceId { get; set; }
    /// <summary>JSON of the look and behaviour the admin chose (colours, background, fonts, history...). Validated against allow-lists on write; never free-form CSS.</summary>
    public string? ThemeJson { get; set; }
    public string TokenHash { get; set; } = "";
    public string? TokenCipher { get; set; }
    /// <summary>Most visitor questions answered per UTC day through this link.</summary>
    public int DailyMessageLimit { get; set; } = 300;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedUtc { get; set; }
}

/// <summary>
/// A named access key for ordinary users of a company: they can read approved knowledge and chat, but cannot upload,
/// review, delete or manage anything. Only a hash is stored; the key is shown once at creation and can be revoked.
/// </summary>
public class TenantAccessKey : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    public string KeyHash { get; set; } = "";
    /// <summary>The key itself, encrypted with the server secret, so the company admin can copy it again later. Null for keys made before this existed, or when no server secret is set. Cleared when the key is revoked.</summary>
    public string? KeyCipher { get; set; }
    /// <summary>The audience this key belongs to. Null = full access to all approved knowledge (how every key worked before audiences existed).</summary>
    public Guid? AudienceId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedUtc { get; set; }
}

public class ConversationMessage : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConversationId { get; set; }
    /// <summary>"user" or "assistant".</summary>
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>Assistant messages only: false when the answer was "not documented".</summary>
    public bool Grounded { get; set; }
    /// <summary>Assistant messages only: JSON array of {document, page} that the answer cites.</summary>
    public string? CitationsJson { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
