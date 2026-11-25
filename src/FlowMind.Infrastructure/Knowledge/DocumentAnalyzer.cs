using System.Security.Cryptography;
using System.Text;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.Knowledge;

/// <summary>
/// Analysis stage for one document version: split the extracted pages into small windows, send each window to the
/// AI once (cached by content hash), validate the output, then write linked knowledge for human review.
/// Never sends the whole document in one call and never calls the provider for content it has already seen.
/// </summary>
public sealed class DocumentAnalyzer
{
    private readonly FlowMindDbContext _db;
    private readonly IDocumentAnalysisProvider _ai;
    private readonly IAiUsageService _usage;
    private readonly KnowledgeWriter _writer;
    private readonly AnalysisOptions _opt;
    private readonly GeminiOptions _gemini;
    private readonly ILogger<DocumentAnalyzer> _log;

    public DocumentAnalyzer(FlowMindDbContext db, IDocumentAnalysisProvider ai, IAiUsageService usage, KnowledgeWriter writer,
        IOptions<AnalysisOptions> opt, IOptions<AiProvidersOptions> providers, ILogger<DocumentAnalyzer> log)
    {
        _db = db; _ai = ai; _usage = usage; _writer = writer; _opt = opt.Value; _gemini = providers.Value.Gemini; _log = log;
    }

    public async Task ProcessAsync(DocumentProcessingJob job, CancellationToken ct)
    {
        var version = await _db.DocumentVersions.FirstOrDefaultAsync(v => v.Id == job.DocumentVersionId, ct)
            ?? throw new UnreadableDocumentException("Document version no longer exists.");
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == version.DocumentId, ct)
            ?? throw new UnreadableDocumentException("Document was deleted.");

        doc.Status = DocumentStatus.Analyzing;
        job.Stage = JobStage.Analysis;
        await _db.SaveChangesAsync(ct);

        var pages = await _db.DocumentPages
            .Where(p => p.DocumentVersionId == version.Id && p.Source != PageTextSource.NeedsOcr)
            .OrderBy(p => p.PageNumber).Select(p => new { p.PageNumber, p.Text }).ToListAsync(ct);

        var windows = BuildWindows(pages.Where(p => !string.IsNullOrWhiteSpace(p.Text)).Select(p => (p.PageNumber, p.Text)), _opt.WindowChars);
        var results = new List<AiAnalysis>();
        var knownModules = new List<string>(); // only modules found earlier in THIS run → deterministic cache keys

        try
        {
            foreach (var w in windows)
            {
                var hint = knownModules.Count == 0 ? "" : "Modules already identified (reuse these exact names where they apply): " + string.Join("; ", knownModules.Take(30)) + "\n";
                var content = hint + "<document>\n" + w + "</document>";

                var analysis = await AnalyzeWindowAsync(version.TenantId, content, ct);
                results.Add(analysis);
                foreach (var m in (analysis.Modules ?? new()).Select(m => m.Name).Concat((analysis.Pages ?? new()).Select(p => p.Module)))
                    if (!string.IsNullOrWhiteSpace(m) && !knownModules.Contains(m!, StringComparer.OrdinalIgnoreCase)) knownModules.Add(m!);
            }
        }
        catch (Exception ex) when (ex is QuotaExceededException or ProviderNotConfiguredException)
        {
            await _usage.RecordAsync(new AIUsageLog
            {
                TenantId = version.TenantId, ProviderKind = AiProviderKind.DocumentAnalysis, Provider = _ai.Name, Model = _gemini.Model,
                Operation = "analyze", Purpose = "document-analysis",
                Status = ex is QuotaExceededException ? AiRequestStatus.QuotaExceeded : AiRequestStatus.Failed,
                ErrorMessage = Truncate(ex.Message, 1000)
            }, ct);
            doc.Status = DocumentStatus.AwaitingAnalysis;
            job.Status = JobStatus.QuotaExceeded;
            job.Error = Truncate($"AI analysis paused after {results.Count}/{windows.Count} sections (finished sections are cached and will not be re-sent): {ex.Message}", 2000);
            job.CompletedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return;
        }

        var stats = await _writer.ApplyAsync(version.TenantId, doc.ApplicationId, version.Id, results, ct);
        _log.LogInformation("Analysis of version {Version}: {Windows} sections → {Stats}", version.Id, windows.Count, stats);

        doc.Status = DocumentStatus.PendingReview;
        job.Status = JobStatus.Succeeded;
        job.Error = null;
        job.CompletedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task<AiAnalysis> AnalyzeWindowAsync(Guid tenantId, string content, CancellationToken ct)
    {
        var key = CacheKey(tenantId, _ai.Name, _gemini.Model, content);

        var cached = await _db.AnalysisCache.FirstOrDefaultAsync(c => c.Key == key, ct);
        if (cached != null)
        {
            await _usage.RecordAsync(new AIUsageLog
            {
                TenantId = tenantId, ProviderKind = AiProviderKind.DocumentAnalysis, Provider = _ai.Name, Model = _gemini.Model,
                Operation = "analyze", Purpose = "document-analysis", Status = AiRequestStatus.Cached
            }, ct);
            return AnalysisContract.Parse(cached.ResponseJson);
        }

        await _usage.EnsureWithinLimitAsync(AiProviderKind.DocumentAnalysis, _ai.Name, 1, ct);

        DocumentAnalysisResult raw;
        try
        {
            raw = await _ai.AnalyzeAsync(AnalysisContract.SystemPrompt, content, AnalysisContract.JsonSchema, ct);
        }
        catch (Exception ex) when (ex is TransientProviderException or InvalidAnalysisOutputException)
        {
            await _usage.RecordAsync(new AIUsageLog
            {
                TenantId = tenantId, ProviderKind = AiProviderKind.DocumentAnalysis, Provider = _ai.Name, Model = _gemini.Model,
                Operation = "analyze", Purpose = "document-analysis", Status = AiRequestStatus.Failed, ErrorMessage = Truncate(ex.Message, 1000)
            }, ct);
            throw;
        }

        AiAnalysis parsed;
        try { parsed = AnalysisContract.Parse(raw.Json); }
        catch (InvalidAnalysisOutputException ex)
        {
            await _usage.RecordAsync(new AIUsageLog
            {
                TenantId = tenantId, ProviderKind = AiProviderKind.DocumentAnalysis, Provider = _ai.Name, Model = _gemini.Model,
                Operation = "analyze", Purpose = "document-analysis", InputTokens = raw.InputTokens, OutputTokens = raw.OutputTokens,
                Status = AiRequestStatus.Failed, ErrorMessage = Truncate(ex.Message, 1000)
            }, ct);
            throw;
        }

        // Cache only validated output, and log usage in the same save.
        _db.AnalysisCache.Add(new AnalysisCacheEntry
        {
            Key = key, TenantId = tenantId, Provider = _ai.Name, Model = _gemini.Model, PromptVersion = AnalysisContract.PromptVersion,
            ResponseJson = raw.Json, InputTokens = raw.InputTokens, OutputTokens = raw.OutputTokens
        });
        await _usage.RecordAsync(new AIUsageLog
        {
            TenantId = tenantId, ProviderKind = AiProviderKind.DocumentAnalysis, Provider = _ai.Name, Model = _gemini.Model,
            Operation = "analyze", Purpose = "document-analysis", InputTokens = raw.InputTokens, OutputTokens = raw.OutputTokens,
            Status = AiRequestStatus.Success
        }, ct);
        return parsed;
    }

    /// <summary>Greedy packing of whole pages into windows of at most maxChars; an oversized page is truncated.</summary>
    public static List<string> BuildWindows(IEnumerable<(int PageNumber, string Text)> pages, int maxChars)
    {
        var windows = new List<string>();
        var sb = new StringBuilder();
        foreach (var (num, text) in pages)
        {
            var body = text.Length > maxChars ? text[..maxChars] : text;
            var block = $"[Page {num}]\n{body}\n\n";
            if (sb.Length > 0 && sb.Length + block.Length > maxChars)
            {
                windows.Add(sb.ToString());
                sb.Clear();
            }
            sb.Append(block);
        }
        if (sb.Length > 0) windows.Add(sb.ToString());
        return windows;
    }

    public static string CacheKey(Guid tenantId, string provider, string model, string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{tenantId:N}|{provider}|{model}|{AnalysisContract.PromptVersion}|{content}"))).ToLowerInvariant();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
