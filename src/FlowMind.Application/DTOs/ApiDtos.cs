using System.ComponentModel.DataAnnotations;

namespace FlowMind.Application.DTOs;

// ---- Auth ----
public sealed class TokenRequest
{
    /// <summary>Tenant slug. Omit to authenticate as platform administrator.</summary>
    public string? TenantSlug { get; set; }
    [Required, MaxLength(200)] public string ApiKey { get; set; } = "";
}
public sealed record TokenResponse(string AccessToken, string TokenType, DateTime ExpiresUtc);

// ---- Tenants ----
public sealed class CreateTenantRequest
{
    [Required, StringLength(200, MinimumLength = 2)] public string Name { get; set; } = "";
    [Required, RegularExpression("^[a-z0-9][a-z0-9-]{1,98}$", ErrorMessage = "Slug must be 2-99 chars: lowercase letters, digits, hyphens.")]
    public string Slug { get; set; } = "";
    /// <summary>Optional: invite the company's first administrator by e-mail (they choose their own password).</summary>
    [MaxLength(254)] public string? AdminEmail { get; set; }
    [MaxLength(100)] public string? AdminName { get; set; }
}
/// <summary>ApiKey is returned only once, at creation.</summary>
/// <param name="InviteLink">Set when a company admin was invited but no e-mail could be sent: the platform admin passes the link on.</param>
public sealed record CreateTenantResponse(Guid Id, string Name, string Slug, string ApiKey, string? AdminEmail = null, string? InviteLink = null, bool InviteEmailed = false);

// ---- Applications ----
public sealed class CreateApplicationRequest
{
    [Required, StringLength(200, MinimumLength = 2)] public string Name { get; set; } = "";
    [StringLength(2000)] public string? Description { get; set; }
}
public sealed record ApplicationResponse(Guid Id, string Name, string? Description, DateTime CreatedUtc);

// ---- Documents ----
public sealed record UploadDocumentResponse(Guid DocumentId, int VersionNumber, string Status, bool Duplicate, string ContentHash);

public sealed record JobInfo(string Stage, string Status, int Attempts, string? Error, DateTime CreatedUtc, DateTime? StartedUtc, DateTime? CompletedUtc);

public sealed record DocumentStatusResponse(
    Guid Id, string FileName, string Status, int? VersionNumber, int? PageCount,
    int PagesDigital, int PagesOcr, int PagesAwaitingOcr, string? ContentHash, JobInfo? LatestJob);

public sealed record DocumentSummary(Guid Id, Guid ApplicationId, string FileName, string Status, DateTime CreatedUtc);

public sealed record PageResponse(int PageNumber, string Source, bool HasImages, int CharCount, string Text);

// ---- Usage ----
/// <param name="Kind">What the calls were for: DocumentAnalysis, Chat, Embedding or Ocr (so Gemini chat is not mixed up with Gemini analysis).</param>
public sealed record UsageLine(string Provider, string Model, int Requests, int InputTokens, int OutputTokens, int OcrPages, decimal EstimatedCostUsd, string Kind = "");
/// <summary>
/// One daily cap that FlowMind enforces on a provider. <c>Used</c> counts everything on this server (all companies), because
/// the cap protects one shared provider allowance. Status: off (limit 0), ok, warn (80% or more), full (100% or more).
/// </summary>
public sealed record LimitLine(
    string Provider, string Metric, string Unit, long Limit, double Used, double Percent, string Status,
    DateTime PeriodStartUtc, DateTime ResetsAtUtc, string ResetBasis, int? PerMinute,
    double MonthUsed, long MonthCapacity, string MonthlyNote);

public sealed record UsageResponse(DateTime TodayUtc, IReadOnlyList<UsageLine> Today, IReadOnlyList<UsageLine> ThisMonth, IReadOnlyList<LimitLine> Limits);
