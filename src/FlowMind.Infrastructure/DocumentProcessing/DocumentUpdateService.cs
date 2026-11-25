using System.Globalization;
using System.Text;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.Infrastructure.DocumentProcessing;

public sealed class DocumentUpdateRejectedException : Exception
{
    public int StatusCode { get; }
    public DocumentUpdateRejectedException(string message, int statusCode = 400) : base(message) { StatusCode = statusCode; }
}

public sealed record DocumentUpdateResult(int AddedPages, int TotalPages, int Characters);

/// <summary>
/// Adds a small update (pasted text or a small file) to a document that is already uploaded, without uploading the whole file again.
/// The new text is appended as extra pages of the document's current version, each headed "UPDATE date - title" so the assistant
/// can tell it is newer. Old pages, their cached AI analysis, their approved knowledge and their stored search chunks are kept as they
/// are, so only the new text is analysed (and later embedded) and only the new items need review.
/// </summary>
public sealed class DocumentUpdateService
{
    public const int MaxTextChars = 20_000;
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxFilePages = 20;
    public const int MaxTitleChars = 120;
    private const int PageChars = 3500;

    private readonly FlowMindDbContext _db;
    private readonly IEnumerable<IDocumentTextExtractor> _extractors;

    public DocumentUpdateService(FlowMindDbContext db, IEnumerable<IDocumentTextExtractor> extractors) { _db = db; _extractors = extractors; }

    public Task<DocumentUpdateResult> AddTextAsync(Guid documentId, string? title, string? text, CancellationToken ct)
    {
        var body = Clean(text);
        if (body.Length == 0) throw new DocumentUpdateRejectedException("Write the update text first.");
        if (body.Length > MaxTextChars) throw new DocumentUpdateRejectedException($"An update can have at most {MaxTextChars:N0} characters. Split it into smaller updates, or upload the whole document again if most of it changed.");
        return AppendAsync(documentId, title, new[] { body }, ct);
    }

    public async Task<DocumentUpdateResult> AddFileAsync(Guid documentId, string? title, Stream content, string extension, CancellationToken ct)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        var extractor = _extractors.FirstOrDefault(e => e.CanHandle(ext))
            ?? throw new DocumentUpdateRejectedException("Unsupported file type. Allowed: PDF, DOCX, TXT.", 415);
        IReadOnlyList<ExtractedPage> pages;
        try { pages = extractor.Extract(content); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new DocumentUpdateRejectedException("That file could not be read."); }

        if (pages.Count > MaxFilePages) throw new DocumentUpdateRejectedException($"An update file can have at most {MaxFilePages} pages. For larger changes upload the whole document again.");
        if (pages.Any(p => p.NeedsOcr)) throw new DocumentUpdateRejectedException("This file has scanned pages. Updates need a file with real text (a normal PDF, DOCX or TXT), or paste the text instead.");
        var bodies = pages.Select(p => Clean(p.Text)).Where(t => t.Length > 0).ToList();
        if (bodies.Count == 0) throw new DocumentUpdateRejectedException("No text was found in that file.");
        if (bodies.Sum(b => b.Length) > MaxTextChars * 2) throw new DocumentUpdateRejectedException("That file has too much text for an update. Upload the whole document again instead.");
        return await AppendAsync(documentId, title, bodies, ct);
    }

    private async Task<DocumentUpdateResult> AppendAsync(Guid documentId, string? title, IReadOnlyList<string> bodies, CancellationToken ct)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.DeletedUtc == null, ct);
        if (doc?.CurrentVersionId is not { } versionId) throw new DocumentUpdateRejectedException("Document not found.", 404);
        if (doc.Status is not (DocumentStatus.Approved or DocumentStatus.PendingReview))
            throw new DocumentUpdateRejectedException($"This document is {doc.Status} right now. Wait until its processing has finished, then add the update.", 409);
        if (await _db.ProcessingJobs.AnyAsync(j => j.DocumentVersionId == versionId && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct))
            throw new DocumentUpdateRejectedException("This document is still being processed. Try again in a minute.", 409);
        var version = await _db.DocumentVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct)
            ?? throw new DocumentUpdateRejectedException("Document not found.", 404);

        var label = Header(title);
        var parts = bodies.SelectMany(b => Split(b)).ToList();
        var next = (await _db.DocumentPages.Where(p => p.DocumentVersionId == versionId).MaxAsync(p => (int?)p.PageNumber, ct) ?? 0) + 1;
        var chars = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            var text = label + (parts.Count > 1 ? $" (part {i + 1} of {parts.Count})" : "") + "\n\n" + parts[i];
            _db.DocumentPages.Add(new DocumentPage
            {
                TenantId = doc.TenantId, DocumentVersionId = versionId, PageNumber = next + i,
                Text = text, Source = PageTextSource.Digital, HasImages = false, CharCount = text.Length
            });
            chars += text.Length;
        }
        version.PageCount = next - 1 + parts.Count;

        // Back into the normal pipeline at the analysis step: it re-reads all pages but old sections come from the cache, so only new text is sent to the AI.
        doc.Status = DocumentStatus.Extracted;
        _db.ProcessingJobs.Add(new DocumentProcessingJob { TenantId = doc.TenantId, DocumentVersionId = versionId, Stage = JobStage.Analysis });
        await _db.SaveChangesAsync(ct);
        return new DocumentUpdateResult(parts.Count, version.PageCount ?? parts.Count, chars);
    }

    private static string Header(string? title)
    {
        var t = new string((title ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (t.Length > MaxTitleChars) t = t[..MaxTitleChars].TrimEnd();
        var date = DateTime.UtcNow.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        return t.Length == 0 ? $"UPDATE {date}" : $"UPDATE {date} - {t}";
    }

    /// <summary>Normalises line breaks and drops control characters (the text is untrusted; it is only ever treated as document text).</summary>
    internal static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Replace("\r\n", "\n").Replace('\r', '\n'))
            if (c == '\n' || c == '\t' || !char.IsControl(c)) sb.Append(c);
        return sb.ToString().Trim();
    }

    /// <summary>Splits long text into pages of about PageChars at paragraph or sentence breaks, so no page is cut short by the analysis window.</summary>
    internal static List<string> Split(string text)
    {
        var pages = new List<string>();
        var sb = new StringBuilder();
        void Flush() { if (sb.Length > 0) { pages.Add(sb.ToString().Trim()); sb.Clear(); } }
        foreach (var para in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var p = para.Trim();
            while (p.Length > PageChars)
            {
                var cut = p.LastIndexOf(' ', PageChars - 1);
                if (cut < PageChars / 2) cut = PageChars;
                if (sb.Length > 0) Flush();
                pages.Add(p[..cut].Trim());
                p = p[cut..].TrimStart();
            }
            if (p.Length == 0) continue;
            if (sb.Length > 0 && sb.Length + 2 + p.Length > PageChars) Flush();
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(p);
        }
        Flush();
        return pages;
    }
}
