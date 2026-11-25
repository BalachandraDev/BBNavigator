using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.Retrieval;

/// <summary>
/// Embedding stage for one APPROVED document version: chunk the extracted page text, embed only chunks that are
/// not already stored (resumable after a quota stop, free to re-run), and write them to the vector store.
/// Only approved documents are ever searchable, so the chatbot cannot answer from unreviewed material.
/// </summary>
public sealed class DocumentIndexer
{
    private readonly FlowMindDbContext _db;
    private readonly IVectorStore _store;
    private readonly EmbeddingService _embeddings;
    private readonly RetrievalOptions _opt;
    private readonly ILogger<DocumentIndexer> _log;

    public DocumentIndexer(FlowMindDbContext db, IVectorStore store, EmbeddingService embeddings,
        IOptions<RetrievalOptions> opt, ILogger<DocumentIndexer> log)
    {
        _db = db; _store = store; _embeddings = embeddings; _opt = opt.Value; _log = log;
    }

    public async Task ProcessAsync(DocumentProcessingJob job, CancellationToken ct)
    {
        var version = await _db.DocumentVersions.FirstOrDefaultAsync(v => v.Id == job.DocumentVersionId, ct)
            ?? throw new UnreadableDocumentException("Document version no longer exists.");
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == version.DocumentId, ct);
        var tenantId = version.TenantId;

        if (doc == null || doc.DeletedUtc != null)
        {
            await _store.DeleteDocumentAsync(tenantId, version.DocumentId, ct);
            Finish(job, "Skipped: document was deleted.");
            await _db.SaveChangesAsync(ct);
            return;
        }
        if (doc.Status != DocumentStatus.Approved || doc.CurrentVersionId != version.Id)
        {
            Finish(job, "Skipped: only the approved, current version of a document is indexed.");
            await _db.SaveChangesAsync(ct);
            return;
        }

        var pages = await _db.DocumentPages
            .Where(p => p.DocumentVersionId == version.Id && p.Source != PageTextSource.NeedsOcr)
            .OrderBy(p => p.PageNumber).Select(p => new { p.PageNumber, p.Text }).ToListAsync(ct);
        var chunks = Chunker.Build(pages.Select(p => (p.PageNumber, p.Text)), _opt.ChunkChars, _opt.OverlapChars);

        // Drop anything stored for this version that no longer exists, then embed only what is missing or changed.
        await _store.DeleteVersionChunksNotInAsync(tenantId, version.Id, chunks.Select(c => c.Index).ToList(), ct);
        var existing = await _store.GetIndexedHashesAsync(tenantId, version.Id, ct);
        var todo = chunks.Where(c => !existing.TryGetValue(c.Index, out var h) || h != c.Hash).ToList();

        int done = 0;
        try
        {
            foreach (var batch in todo.Chunk(_embeddings.BatchSize))
            {
                var vectors = await _embeddings.EmbedAsync(tenantId, batch.Select(c => c.Content).ToList(), "document-indexing", ct);
                await _store.UpsertAsync(tenantId, batch.Select((c, i) => new ChunkRecord(
                    Guid.NewGuid(), doc.ApplicationId, doc.Id, version.Id, doc.FileName, c.Index, c.PageNumber, c.Content, c.Hash, vectors[i])).ToList(), ct);
                done += batch.Length;
            }
        }
        catch (Exception ex) when (ex is QuotaExceededException or ProviderNotConfiguredException)
        {
            job.Status = JobStatus.QuotaExceeded;
            job.Error = Truncate($"Indexing paused after {existing.Count + done}/{chunks.Count} sections (stored sections are kept and will not be re-sent): {ex.Message}", 2000);
            job.CompletedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return;
        }

        await _store.DeleteOtherVersionsAsync(tenantId, doc.Id, version.Id, ct);
        _log.LogInformation("Indexed version {Version}: {Total} chunks ({New} newly embedded)", version.Id, chunks.Count, done);
        Finish(job, null);
        await _db.SaveChangesAsync(ct);
    }

    private static void Finish(DocumentProcessingJob job, string? note)
    {
        job.Status = JobStatus.Succeeded;
        job.Error = note;
        job.CompletedUtc = DateTime.UtcNow;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
