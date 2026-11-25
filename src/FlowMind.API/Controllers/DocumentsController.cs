using System.ComponentModel.DataAnnotations;
using FlowMind.Application.DTOs;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlowMind.API.Controllers;

public sealed class UploadOptions
{
    public const string Section = "Upload";
    public long MaxBytes { get; set; } = 25 * 1024 * 1024;
}

public sealed class UploadDocumentRequest
{
    [Required] public Guid ApplicationId { get; set; }
    [Required] public IFormFile File { get; set; } = default!;
}

public sealed class AddTextUpdateRequest
{
    [MaxLength(120)] public string? Title { get; set; }
    [Required] public string? Text { get; set; }
}

public sealed class AddFileUpdateRequest
{
    [MaxLength(120)] public string? Title { get; set; }
    [Required] public IFormFile File { get; set; } = default!;
}

public sealed record DocumentUpdateResponse(int AddedPages, int TotalPages);

[ApiController]
[Route("api/v1/documents")]
[Authorize(Policy = "TenantAdmin")]
public class DocumentsController : ControllerBase
{
    // Server decides the content type from the validated extension; the client-supplied one is ignored.
    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = "application/pdf",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["txt"] = "text/plain"
    };

    private readonly FlowMindDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IFileStorage _storage;
    private readonly IAuditLogger _audit;
    private readonly UploadOptions _upload;
    private readonly ILogger<DocumentsController> _log;
    private readonly IVectorStore _vectors;
    private readonly FlowMind.Infrastructure.DocumentProcessing.DocumentUpdateService _updates;

    public DocumentsController(FlowMindDbContext db, ITenantContext tenant, IFileStorage storage, IAuditLogger audit,
        IOptions<UploadOptions> upload, ILogger<DocumentsController> log, IVectorStore vectors,
        FlowMind.Infrastructure.DocumentProcessing.DocumentUpdateService updates)
    {
        _db = db; _tenant = tenant; _storage = storage; _audit = audit; _upload = upload.Value; _log = log; _vectors = vectors; _updates = updates;
    }

    /// <summary>
    /// Upload a PDF, DOCX or TXT. Identical content already uploaded to the same application is not stored or
    /// processed again. A new file with an existing name becomes a new version of that document.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<UploadDocumentResponse>> Upload([FromForm] UploadDocumentRequest req, CancellationToken ct)
    {
        var tenantId = _tenant.TenantId!.Value;
        var file = req.File;

        if (file.Length == 0) return Problem("The file is empty.", statusCode: 400);
        if (file.Length > _upload.MaxBytes) return Problem($"File exceeds the {_upload.MaxBytes / (1024 * 1024)} MB limit.", statusCode: 413);

        var fileName = SanitizeFileName(file.FileName);
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (!AllowedTypes.TryGetValue(ext, out var contentType))
            return Problem("Unsupported file type. Allowed: PDF, DOCX, TXT.", statusCode: 415);

        if (!await HasValidSignatureAsync(file, ext, ct))
            return Problem("File content does not match its extension.", statusCode: 415);

        if (!await _db.Applications.AnyAsync(a => a.Id == req.ApplicationId, ct)) // tenant-filtered
            return Problem("Application not found.", statusCode: 404);

        StagedFile staged;
        try
        {
            await using var input = file.OpenReadStream();
            staged = await _storage.StageAsync(input, _upload.MaxBytes, ct);
        }
        catch (InvalidDataException ex) { return Problem(ex.Message, statusCode: 413); }

        // De-duplication: same content already in this application → reuse, no storage, no processing, no AI cost.
        var existing = await _db.Documents
            .Where(d => d.ApplicationId == req.ApplicationId && d.Versions.Any(v => v.ContentHash == staged.Sha256))
            .Select(d => new { d.Id, d.Status, Version = d.Versions.First(v => v.ContentHash == staged.Sha256).VersionNumber })
            .FirstOrDefaultAsync(ct);
        if (existing != null)
        {
            _storage.Discard(staged);
            return Ok(new UploadDocumentResponse(existing.Id, existing.Version, existing.Status.ToString(), true, staged.Sha256));
        }

        var doc = await _db.Documents.Include(d => d.Versions)
            .FirstOrDefaultAsync(d => d.ApplicationId == req.ApplicationId && d.FileName == fileName, ct);
        if (doc == null)
        {
            doc = new Document { TenantId = tenantId, ApplicationId = req.ApplicationId, FileName = fileName, ContentType = contentType };
            _db.Documents.Add(doc);
        }
        var versionNumber = doc.Versions.Count == 0 ? 1 : doc.Versions.Max(v => v.VersionNumber) + 1;

        StoredFile stored;
        try { stored = await _storage.CommitAsync(staged, tenantId, doc.Id, versionNumber, ext, ct); }
        catch (FlowMind.Infrastructure.Storage.StorageUnavailableException ex)
        {
            _storage.Discard(staged);
            return Problem(ex.Message, statusCode: 503);   // the company's chosen location is not usable: say so instead of quietly saving elsewhere
        }
        try
        {
            var version = new DocumentVersion
            {
                TenantId = tenantId, DocumentId = doc.Id, VersionNumber = versionNumber,
                ContentHash = staged.Sha256, SizeBytes = staged.SizeBytes, StoragePath = stored.Path, StorageKind = stored.Kind
            };
            _db.DocumentVersions.Add(version);
            _db.ProcessingJobs.Add(new DocumentProcessingJob { TenantId = tenantId, DocumentVersionId = version.Id, Stage = JobStage.Extraction });
            doc.CurrentVersionId = version.Id;
            doc.Status = DocumentStatus.Queued;
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            try { await _storage.DeleteVersionAsync(tenantId, doc.Id, versionNumber, CancellationToken.None); } catch { /* best effort */ }
            throw;
        }

        await _audit.LogAsync("document.upload", "Document", doc.Id.ToString(), $"{fileName} v{versionNumber}", ct: ct);
        return AcceptedAtAction(nameof(Status), new { id = doc.Id },
            new UploadDocumentResponse(doc.Id, versionNumber, doc.Status.ToString(), false, staged.Sha256));
    }

    [HttpGet]
    public async Task<IReadOnlyList<DocumentSummary>> List([FromQuery] Guid? applicationId, CancellationToken ct) =>
        await _db.Documents.Where(d => applicationId == null || d.ApplicationId == applicationId)
            .OrderByDescending(d => d.CreatedUtc)
            .Select(d => new DocumentSummary(d.Id, d.ApplicationId, d.FileName, d.Status.ToString(), d.CreatedUtc))
            .Take(200).ToListAsync(ct);

    [HttpGet("{id:guid}/status")]
    public async Task<ActionResult<DocumentStatusResponse>> Status(Guid id, CancellationToken ct)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc == null) return NotFound();

        var version = doc.CurrentVersionId is { } vid ? await _db.DocumentVersions.FirstOrDefaultAsync(v => v.Id == vid, ct) : null;
        int digital = 0, ocr = 0, waiting = 0;
        JobInfo? job = null;
        if (version != null)
        {
            var counts = await _db.DocumentPages.Where(p => p.DocumentVersionId == version.Id)
                .GroupBy(p => p.Source).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
            digital = counts.FirstOrDefault(c => c.Key == PageTextSource.Digital)?.N ?? 0;
            ocr = counts.FirstOrDefault(c => c.Key == PageTextSource.Ocr)?.N ?? 0;
            waiting = counts.FirstOrDefault(c => c.Key == PageTextSource.NeedsOcr)?.N ?? 0;

            job = await _db.ProcessingJobs.Where(j => j.DocumentVersionId == version.Id)
                .OrderByDescending(j => j.CreatedUtc)
                .Select(j => new JobInfo(j.Stage.ToString(), j.Status.ToString(), j.Attempts, j.Error, j.CreatedUtc, j.StartedUtc, j.CompletedUtc))
                .FirstOrDefaultAsync(ct);
        }
        return new DocumentStatusResponse(doc.Id, doc.FileName, doc.Status.ToString(), version?.VersionNumber, version?.PageCount,
            digital, ocr, waiting, version?.ContentHash, job);
    }

    /// <summary>Extracted text per page for the current version (used to review extraction quality).</summary>
    [HttpGet("{id:guid}/pages")]
    public async Task<ActionResult<IReadOnlyList<PageResponse>>> Pages(Guid id, [FromQuery] int from = 1, [FromQuery, Range(1, 100)] int take = 50, CancellationToken ct = default)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc?.CurrentVersionId == null) return NotFound();
        return await _db.DocumentPages
            .Where(p => p.DocumentVersionId == doc.CurrentVersionId && p.PageNumber >= from)
            .OrderBy(p => p.PageNumber).Take(take)
            .Select(p => new PageResponse(p.PageNumber, p.Source.ToString(), p.HasImages, p.CharCount, p.Text))
            .ToListAsync(ct);
    }

    /// <summary>Re-queue a document that is awaiting OCR (e.g. after quota resets) or failed. Already-extracted pages are reused.</summary>
    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc?.CurrentVersionId == null) return NotFound();
        if (doc.Status is not (DocumentStatus.AwaitingOcr or DocumentStatus.AwaitingAnalysis or DocumentStatus.Failed or DocumentStatus.Extracted))
            return Problem($"Document is {doc.Status}; only AwaitingOcr, AwaitingAnalysis, Extracted or Failed documents can be retried.", statusCode: 409);

        if (await _db.ProcessingJobs.AnyAsync(j => j.DocumentVersionId == doc.CurrentVersionId
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct))
            return Problem("A job is already queued or running.", statusCode: 409);

        // Resume at the right stage: extraction if text/OCR is incomplete, otherwise AI analysis.
        // Finished work is never repeated (extracted pages and cached AI responses are reused).
        var versionId = doc.CurrentVersionId.Value;
        var extractionIncomplete = !await _db.DocumentPages.AnyAsync(p => p.DocumentVersionId == versionId, ct)
            || await _db.DocumentPages.AnyAsync(p => p.DocumentVersionId == versionId && p.Source == PageTextSource.NeedsOcr, ct);
        var stage = extractionIncomplete ? JobStage.Extraction : JobStage.Analysis;

        _db.ProcessingJobs.Add(new DocumentProcessingJob { TenantId = doc.TenantId, DocumentVersionId = versionId, Stage = stage });
        doc.Status = DocumentStatus.Queued;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("document.retry", "Document", doc.Id.ToString(), ct: ct);
        return Accepted();
    }

    /// <summary>
    /// (Re)queue indexing of an approved document, e.g. after the daily embedding quota reset.
    /// Chunks already stored are kept and not embedded again.
    /// </summary>
    [HttpPost("{id:guid}/reindex")]
    public async Task<IActionResult> Reindex(Guid id, CancellationToken ct)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc?.CurrentVersionId == null) return NotFound();
        if (doc.Status != DocumentStatus.Approved)
            return Problem($"Document is {doc.Status}; only approved documents are indexed.", statusCode: 409);
        var v = doc.CurrentVersionId.Value;
        if (await _db.ProcessingJobs.AnyAsync(j => j.DocumentVersionId == v && j.Stage == JobStage.Embedding
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct))
            return Problem("Indexing is already queued or running.", statusCode: 409);

        _db.ProcessingJobs.Add(new DocumentProcessingJob { TenantId = doc.TenantId, DocumentVersionId = v, Stage = JobStage.Embedding });
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("document.reindex", "Document", doc.Id.ToString(), ct: ct);
        return Accepted();
    }

    /// <summary>Add a small update (pasted text) to an uploaded document instead of uploading the whole file again. Only the new text is analysed.</summary>
    [HttpPost("{id:guid}/updates/text")]
    public async Task<ActionResult<DocumentUpdateResponse>> AddTextUpdate(Guid id, AddTextUpdateRequest req, CancellationToken ct)
    {
        try
        {
            var r = await _updates.AddTextAsync(id, req.Title, req.Text, ct);
            await _audit.LogAsync("document.update", "Document", id.ToString(), $"text, {r.AddedPages} page(s), {r.Characters} chars: {Short(req.Title)}", ct: ct);
            return Accepted(new DocumentUpdateResponse(r.AddedPages, r.TotalPages));
        }
        catch (FlowMind.Infrastructure.DocumentProcessing.DocumentUpdateRejectedException ex) { return Problem(ex.Message, statusCode: ex.StatusCode); }
    }

    /// <summary>Add a small file (PDF, DOCX or TXT with real text, at most 2 MB / 20 pages) holding only the new or changed part.</summary>
    [HttpPost("{id:guid}/updates/file")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<DocumentUpdateResponse>> AddFileUpdate(Guid id, [FromForm] AddFileUpdateRequest req, CancellationToken ct)
    {
        var file = req.File;
        if (file.Length == 0) return Problem("The file is empty.", statusCode: 400);
        if (file.Length > FlowMind.Infrastructure.DocumentProcessing.DocumentUpdateService.MaxFileBytes)
            return Problem("An update file can be at most 2 MB. For larger changes upload the whole document again.", statusCode: 413);
        var ext = Path.GetExtension(SanitizeFileName(file.FileName)).TrimStart('.').ToLowerInvariant();
        if (!AllowedTypes.ContainsKey(ext)) return Problem("Unsupported file type. Allowed: PDF, DOCX, TXT.", statusCode: 415);
        if (!await HasValidSignatureAsync(file, ext, ct)) return Problem("File content does not match its extension.", statusCode: 415);
        try
        {
            await using var s = file.OpenReadStream();
            var r = await _updates.AddFileAsync(id, req.Title, s, ext, ct);
            await _audit.LogAsync("document.update", "Document", id.ToString(), $"file {SanitizeFileName(file.FileName)}, {r.AddedPages} page(s), {r.Characters} chars: {Short(req.Title)}", ct: ct);
            return Accepted(new DocumentUpdateResponse(r.AddedPages, r.TotalPages));
        }
        catch (FlowMind.Infrastructure.DocumentProcessing.DocumentUpdateRejectedException ex) { return Problem(ex.Message, statusCode: ex.StatusCode); }
    }

    private static string Short(string? s) { s = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim(); return s.Length > 80 ? s[..80] : s; }

    /// <summary>Delete a document: original files, extracted text and search vectors are purged immediately; metadata is soft-deleted for audit.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var doc = await _db.Documents.Include(d => d.Versions).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc == null) return NotFound();

        // First, so a vector-store outage fails the request (retryable) instead of leaving deleted content searchable.
        await _vectors.DeleteDocumentAsync(doc.TenantId, doc.Id, ct);

        var versionIds = doc.Versions.Select(v => v.Id).ToList();
        await _db.DocumentPages.Where(p => versionIds.Contains(p.DocumentVersionId)).ExecuteDeleteAsync(ct);
        doc.DeletedUtc = DateTime.UtcNow;
        doc.Status = DocumentStatus.Deleted;
        await _db.SaveChangesAsync(ct);

        try { await _storage.DeleteDocumentAsync(doc.TenantId, doc.Id, ct); }
        catch (Exception ex) { _log.LogError(ex, "Failed to purge files for document {Doc}", doc.Id); }

        await _audit.LogAsync("document.delete", "Document", doc.Id.ToString(), doc.FileName, ct: ct);
        return NoContent();
    }

    private static string SanitizeFileName(string name)
    {
        var n = Path.GetFileName(name ?? "");
        n = new string(n.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return n.Length > 200 ? n[^200..] : n;
    }

    private static async Task<bool> HasValidSignatureAsync(IFormFile file, string ext, CancellationToken ct)
    {
        await using var s = file.OpenReadStream();
        var buf = new byte[8192];
        var n = await s.ReadAsync(buf.AsMemory(0, buf.Length), ct);
        if (n < 4) return false;
        return ext switch
        {
            "pdf" => buf[0] == 0x25 && buf[1] == 0x50 && buf[2] == 0x44 && buf[3] == 0x46,        // %PDF
            "docx" => buf[0] == 0x50 && buf[1] == 0x4B && buf[2] == 0x03 && buf[3] == 0x04,       // ZIP (OOXML)
            "txt" => !buf.AsSpan(0, n).Contains((byte)0),                                          // no NUL bytes
            _ => false
        };
    }
}

[ApiController]
[Route("api/v1/usage")]
[Authorize(Policy = "TenantAdmin")]
public class UsageController : ControllerBase
{
    private readonly FlowMindDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly FlowMind.Infrastructure.AI.AiLimitsReport _limits;
    public UsageController(FlowMindDbContext db, ITenantContext tenant, FlowMind.Infrastructure.AI.AiLimitsReport limits) { _db = db; _tenant = tenant; _limits = limits; }

    /// <summary>AI usage for the calling company (today UTC, month to date) plus every daily cap with its shared usage and next reset.</summary>
    [HttpGet]
    public async Task<UsageResponse> Get(CancellationToken ct)
    {
        var tenantId = _tenant.TenantId;
        var today = DateTime.UtcNow.Date;
        var monthStart = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        async Task<List<UsageLine>> Summarise(DateTime from) =>
            await _db.AiUsageLogs.Where(l => l.TenantId == tenantId && l.TimestampUtc >= from)
                .GroupBy(l => new { l.Provider, l.Model, l.ProviderKind })
                .Select(g => new UsageLine(g.Key.Provider, g.Key.Model, g.Count(), g.Sum(x => x.InputTokens),
                    g.Sum(x => x.OutputTokens), g.Sum(x => x.OcrPages), g.Sum(x => x.EstimatedCostUsd), g.Key.ProviderKind.ToString()))
                .ToListAsync(ct);

        return new UsageResponse(today, await Summarise(today), await Summarise(monthStart), await _limits.GetAsync(ct));
    }
}
