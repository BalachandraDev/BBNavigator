using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.AI;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.DocumentProcessing;

/// <summary>
/// Extraction pipeline for one job: text extraction (once per version) → selective OCR of scanned pages only.
/// Re-running a job is cheap: extracted pages are kept, only still-missing OCR pages are processed.
/// </summary>
public sealed class DocumentProcessor
{
    private readonly FlowMindDbContext _db;
    private readonly IFileStorage _storage;
    private readonly IEnumerable<IDocumentTextExtractor> _extractors;
    private readonly IOcrProvider _ocr;
    private readonly IAiUsageService _usage;
    private readonly ProcessingOptions _opt;
    private readonly MistralOcrOptions _ocrOpt;
    private readonly ILogger<DocumentProcessor> _log;

    public DocumentProcessor(FlowMindDbContext db, IFileStorage storage, IEnumerable<IDocumentTextExtractor> extractors,
        IOcrProvider ocr, IAiUsageService usage, IOptions<ProcessingOptions> opt, IOptions<AiProvidersOptions> ai,
        ILogger<DocumentProcessor> log)
    {
        _db = db; _storage = storage; _extractors = extractors; _ocr = ocr; _usage = usage;
        _opt = opt.Value; _ocrOpt = ai.Value.MistralOcr; _log = log;
    }

    public async Task ProcessAsync(DocumentProcessingJob job, CancellationToken ct)
    {
        var version = await _db.DocumentVersions.FirstOrDefaultAsync(v => v.Id == job.DocumentVersionId, ct)
            ?? throw new UnreadableDocumentException("Document version no longer exists.");
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == version.DocumentId, ct)
            ?? throw new UnreadableDocumentException("Document was deleted.");

        doc.Status = DocumentStatus.Processing;
        job.Stage = JobStage.Extraction;
        await _db.SaveChangesAsync(ct);

        // 1) Extract text once per version.
        if (!await _db.DocumentPages.AnyAsync(p => p.DocumentVersionId == version.Id, ct))
        {
            var ext = Path.GetExtension(version.StoragePath);
            var extractor = _extractors.FirstOrDefault(e => e.CanHandle(ext.TrimStart('.')))
                ?? throw new UnreadableDocumentException($"Unsupported file type '{ext}'.");

            IReadOnlyList<ExtractedPage> extracted;
            await using (var stream = await _storage.OpenReadAsync(version.StoragePath, version.StorageKind, version.TenantId, ct))
                extracted = extractor.Extract(stream);

            foreach (var p in extracted)
            {
                _db.DocumentPages.Add(new DocumentPage
                {
                    TenantId = version.TenantId,
                    DocumentVersionId = version.Id,
                    PageNumber = p.PageNumber,
                    Text = p.Text,
                    HasImages = p.HasImages,
                    CharCount = p.Text.Length,
                    Source = p.NeedsOcr ? PageTextSource.NeedsOcr : PageTextSource.Digital
                });
            }
            version.PageCount = extracted.Count;
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Extracted {Pages} pages from version {Version} ({Scanned} need OCR)",
                extracted.Count, version.Id, extracted.Count(p => p.NeedsOcr));
        }

        // 2) OCR only the pages that need it.
        var pending = await _db.DocumentPages
            .Where(p => p.DocumentVersionId == version.Id && p.Source == PageTextSource.NeedsOcr)
            .OrderBy(p => p.PageNumber).ToListAsync(ct);

        if (pending.Count > 0)
        {
            job.Stage = JobStage.Ocr;
            await _db.SaveChangesAsync(ct);
            try
            {
                await RunOcrAsync(version, doc, pending, ct);
            }
            catch (Exception ex) when (ex is QuotaExceededException or ProviderNotConfiguredException)
            {
                var quota = ex is QuotaExceededException;
                await _usage.RecordAsync(new AIUsageLog
                {
                    TenantId = version.TenantId, ProviderKind = AiProviderKind.Ocr, Provider = _ocr.Name, Model = _ocrOpt.Model,
                    Operation = "ocr", Purpose = "document-extraction",
                    Status = quota ? AiRequestStatus.QuotaExceeded : AiRequestStatus.Failed, ErrorMessage = Truncate(ex.Message, 1000)
                }, ct);

                doc.Status = DocumentStatus.AwaitingOcr;
                job.Status = JobStatus.QuotaExceeded;
                job.Error = Truncate($"{pending.Count} scanned page(s) are waiting for OCR: {ex.Message}", 2000);
                job.CompletedUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                return;
            }
        }

        doc.Status = DocumentStatus.Extracted;
        job.Status = JobStatus.Succeeded;
        job.Error = null;
        job.CompletedUtc = DateTime.UtcNow;

        // Hand over to AI analysis (exactly one analysis job per version at a time).
        if (!await _db.ProcessingJobs.AnyAsync(j => j.DocumentVersionId == version.Id && j.Stage == JobStage.Analysis
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct))
        {
            _db.ProcessingJobs.Add(new DocumentProcessingJob { TenantId = version.TenantId, DocumentVersionId = version.Id, Stage = JobStage.Analysis });
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task RunOcrAsync(DocumentVersion version, Document doc, List<DocumentPage> pending, CancellationToken ct)
    {
        foreach (var batch in pending.Chunk(_opt.OcrBatchPages))
        {
            // Check the daily cap before every batch, so a partially-exhausted day still makes progress.
            await _usage.EnsureWithinLimitAsync(AiProviderKind.Ocr, _ocr.Name, batch.Length, ct);

            var numbers = batch.Select(p => p.PageNumber).ToList();
            IReadOnlyList<OcrPageResult> results;
            await using (var stream = await _storage.OpenReadAsync(version.StoragePath, version.StorageKind, version.TenantId, ct))
                results = await _ocr.OcrAsync(stream, doc.ContentType, numbers, ct);

            foreach (var r in results)
            {
                var page = batch.First(p => p.PageNumber == r.PageNumber);
                page.Text = r.Markdown;
                page.CharCount = r.Markdown.Length;
                page.Source = PageTextSource.Ocr;
            }

            await _usage.RecordAsync(new AIUsageLog
            {
                TenantId = version.TenantId, ProviderKind = AiProviderKind.Ocr, Provider = _ocr.Name, Model = _ocrOpt.Model,
                Operation = "ocr", Purpose = "document-extraction", OcrPages = results.Count,
                EstimatedCostUsd = results.Count / 1000m * _ocrOpt.EstimatedCostPer1000PagesUsd,
                Status = AiRequestStatus.Success
            }, ct); // also saves the page updates above (same context)
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
