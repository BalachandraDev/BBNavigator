using System.ComponentModel.DataAnnotations;

namespace FlowMind.Application.DTOs;

public sealed record SourceRef(string? Document, int? Page);

public sealed record PageBrief(Guid Id, string Name, string? Purpose, string Review);

public sealed record ModuleDto(Guid? Id, string Name, string? Description, string? Review, SourceRef Source, IReadOnlyList<PageBrief> Pages);

public sealed record ModulesResponse(IReadOnlyList<ModuleDto> Modules);

public sealed record ValidationDto(Guid Id, string? Field, string Rule, string Review, SourceRef Source);
public sealed record TextItemDto(Guid Id, string Text, string Review, SourceRef Source);
public sealed record RoleDto(Guid Id, string Role, string Permission, string Review, SourceRef Source);
public sealed record NextPageDto(Guid LinkId, Guid PageId, string PageName, string? Condition, string Review);

public sealed record PageDetailDto(
    Guid Id, string Name, string? Purpose, string? Module, string Review, SourceRef Source,
    IReadOnlyList<TextItemDto> Functionalities, IReadOnlyList<ValidationDto> Validations,
    IReadOnlyList<TextItemDto> BusinessRules, IReadOnlyList<RoleDto> Roles,
    IReadOnlyList<NextPageDto> NextPages, IReadOnlyList<NextPageDto> PreviousPages);

public sealed record WorkflowBrief(Guid Id, string Name, string? Module, string? Description, string Review, int Steps);

public sealed record WorkflowStepDto(Guid Id, int Order, string Name, string? Description, Guid? PageId, string? PageName);
public sealed record WorkflowTransitionDto(Guid Id, Guid FromStepId, string FromStep, Guid ToStepId, string ToStep, string? Condition);
public sealed record WorkflowDetailDto(
    Guid Id, string Name, string? Module, string? Description, string Review, SourceRef Source,
    IReadOnlyList<WorkflowStepDto> Steps, IReadOnlyList<WorkflowTransitionDto> Transitions);

// ---- Review ----
public sealed record ReviewItemDto(string Type, Guid Id, string Title, string? Text, string Review, SourceRef Source);

public sealed class ReviewUpdateRequest
{
    /// <summary>Pending, Approved or Rejected.</summary>
    [Required, RegularExpression("^(Pending|Approved|Rejected)$")] public string Status { get; set; } = "";
    [StringLength(200, MinimumLength = 1)] public string? Name { get; set; }
    [StringLength(2000)] public string? Text { get; set; }
}

public sealed record ApproveDocumentResponse(int Approved);
