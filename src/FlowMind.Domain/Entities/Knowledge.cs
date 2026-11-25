using FlowMind.Domain.Enums;

namespace FlowMind.Domain.Entities;

/// <summary>Common shape of every piece of extracted application knowledge: tenant-owned, reviewable, cited.</summary>
public abstract class KnowledgeItem : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public ReviewStatus Review { get; set; } = ReviewStatus.Pending;
    /// <summary>Document version this fact was extracted from (for citations and bulk approval).</summary>
    public Guid? SourceDocumentVersionId { get; set; }
    public int? SourcePage { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class Module : KnowledgeItem
{
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string? Description { get; set; }
}

/// <summary>A screen/page of the customer's application (named AppPage to avoid clashing with PDF page types).</summary>
public class AppPage : KnowledgeItem
{
    public Guid? ModuleId { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string? Purpose { get; set; }
}

public class PageFunctionality : KnowledgeItem
{
    public Guid AppPageId { get; set; }
    public string Description { get; set; } = "";
}

public class ValidationRule : KnowledgeItem
{
    public Guid AppPageId { get; set; }
    public string? FieldName { get; set; }
    public string RuleText { get; set; } = "";
}

public class BusinessRule : KnowledgeItem
{
    public Guid? ModuleId { get; set; }
    public Guid? AppPageId { get; set; }
    public string Statement { get; set; } = "";
}

public class RolePermission : KnowledgeItem
{
    public Guid? AppPageId { get; set; }
    public string Role { get; set; } = "";
    public string Permission { get; set; } = "";
}

/// <summary>Directed relationship "after page A the user goes to page B".</summary>
public class PageLink : KnowledgeItem
{
    public Guid FromPageId { get; set; }
    public Guid ToPageId { get; set; }
    public string? Condition { get; set; }
}

public class Workflow : KnowledgeItem
{
    public Guid? ModuleId { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string? Description { get; set; }
}

public class WorkflowStep : KnowledgeItem
{
    public Guid WorkflowId { get; set; }
    public int Order { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public Guid? AppPageId { get; set; }
    public string? Description { get; set; }
}

public class WorkflowTransition : KnowledgeItem
{
    public Guid WorkflowId { get; set; }
    public Guid FromStepId { get; set; }
    public Guid ToStepId { get; set; }
    public string? Condition { get; set; }
}

/// <summary>
/// Cached AI analysis response. Key = SHA-256 of (tenant, provider, model, prompt version, content),
/// so unchanged content is never sent to the provider twice. Tenant-scoped on purpose.
/// </summary>
public class AnalysisCacheEntry : ITenantOwned
{
    public string Key { get; set; } = "";
    public Guid TenantId { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string PromptVersion { get; set; } = "";
    public string ResponseJson { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
