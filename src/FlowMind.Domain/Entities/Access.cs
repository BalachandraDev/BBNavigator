namespace FlowMind.Domain.Entities;

/// <summary>
/// A group of people who should see the same part of the documentation (for example Manager, Nurse, Ward).
/// Keys and share links can be given an audience; a key or link with no audience keeps full access.
/// </summary>
public class Audience : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Other words the documents use for this group, comma separated ("sister, charge nurse"). Only used to suggest rules.</summary>
    public string? Aliases { get; set; }
    public string? Description { get; set; }
    /// <summary>
    /// False (the default) = people in this audience who sign in with a key get the chat only: no Knowledge page. True = they also browse the modules and pages
    /// their rules open. Either way, answers and the Knowledge page only ever show what the rules allow.
    /// </summary>
    public bool CanBrowseKnowledge { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Kinds of thing an access rule can be attached to.</summary>
public static class AccessTargetKind
{
    public const string Module = "module";
    public const string Page = "page";
    /// <summary>Document pages that no extracted module or page points at (introductions, glossaries, appendices).</summary>
    public const string Document = "document";
    public static bool IsValid(string? kind) => kind is Module or Page or Document;
}

/// <summary>
/// Who may see one module, page or document. No rule at all = not decided yet: only full-access keys see it.
/// "Everyone" = every audience. Otherwise exactly the audiences listed (none listed = admins and full-access keys only).
/// Rules are explicit and never inherited, so a page can never become visible because of what its module allows.
/// </summary>
public class AccessRule : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string TargetKind { get; set; } = AccessTargetKind.Page;
    public Guid TargetId { get; set; }
    public bool Everyone { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public class AccessRuleAudience : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid RuleId { get; set; }
    public Guid AudienceId { get; set; }
}
