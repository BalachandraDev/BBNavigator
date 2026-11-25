namespace FlowMind.Application.Interfaces;

/// <summary>One searchable piece of an approved document, with its exact citation (document + page).</summary>
public sealed record ChunkRecord(Guid Id, Guid ApplicationId, Guid DocumentId, Guid DocumentVersionId, string DocumentName,
    int ChunkIndex, int PageNumber, string Content, string ContentHash, float[] Embedding);

public sealed record ChunkHit(Guid ChunkId, Guid DocumentId, string DocumentName, int PageNumber, string Content,
    double Score, double Similarity);

/// <summary>
/// The document pages a restricted caller may be answered from. Documents in <see cref="FullDocuments"/> are open on every page;
/// for the others only the listed (document, page) pairs are. Anything not listed is never searched at all.
/// </summary>
public sealed record PageAccess(IReadOnlyCollection<Guid> FullDocuments, IReadOnlyCollection<(Guid DocumentId, int Page)> Pages)
{
    private readonly HashSet<Guid> _full = new(FullDocuments);
    private readonly HashSet<(Guid, int)> _pages = new(Pages.Select(p => (p.DocumentId, p.Page)));
    public bool Allows(Guid documentId, int page) => _full.Contains(documentId) || _pages.Contains((documentId, page));
}

/// <summary>
/// Tenant-scoped vector storage. Every method takes the tenant explicitly and filters on it in SQL;
/// there is deliberately no overload that works without one.
/// </summary>
public interface IVectorStore
{
    Task<Dictionary<int, string>> GetIndexedHashesAsync(Guid tenantId, Guid versionId, CancellationToken ct);
    Task UpsertAsync(Guid tenantId, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct);
    Task DeleteVersionChunksNotInAsync(Guid tenantId, Guid versionId, IReadOnlyCollection<int> keepIndexes, CancellationToken ct);
    Task DeleteOtherVersionsAsync(Guid tenantId, Guid documentId, Guid keepVersionId, CancellationToken ct);
    Task DeleteDocumentAsync(Guid tenantId, Guid documentId, CancellationToken ct);
    /// <summary>Removes every search vector a company owns.</summary>
    Task DeleteTenantAsync(Guid tenantId, CancellationToken ct);
    /// <summary>Hybrid search: vector similarity + keyword match, fused with reciprocal-rank fusion.</summary>
    Task<IReadOnlyList<ChunkHit>> SearchAsync(Guid tenantId, Guid applicationId, string queryText, float[] queryEmbedding, int topK, CancellationToken ct);
    /// <summary>Same search, limited to some documents (a share link scoped to specific modules). Null = every document of the application.</summary>
    async Task<IReadOnlyList<ChunkHit>> SearchInAsync(Guid tenantId, Guid applicationId, string queryText, float[] queryEmbedding, int topK, IReadOnlyCollection<Guid>? documentIds, CancellationToken ct)
    {
        if (documentIds == null) return await SearchAsync(tenantId, applicationId, queryText, queryEmbedding, topK, ct);
        var all = await SearchAsync(tenantId, applicationId, queryText, queryEmbedding, topK * 5, ct);
        return all.Where(h => documentIds.Contains(h.DocumentId)).Take(topK).ToList();
    }
    /// <summary>
    /// Same search, also limited to the pages an audience may see. Null access = no page limit. The real store applies the limit inside the
    /// query; this fallback (used by simple stores) over-fetches and drops anything not allowed, so nothing outside the limit is ever returned.
    /// </summary>
    async Task<IReadOnlyList<ChunkHit>> SearchScopedAsync(Guid tenantId, Guid applicationId, string queryText, float[] queryEmbedding, int topK,
        IReadOnlyCollection<Guid>? documentIds, PageAccess? access, CancellationToken ct)
    {
        if (access == null) return await SearchInAsync(tenantId, applicationId, queryText, queryEmbedding, topK, documentIds, ct);
        var all = await SearchInAsync(tenantId, applicationId, queryText, queryEmbedding, topK * 8, documentIds, ct);
        return all.Where(h => access.Allows(h.DocumentId, h.PageNumber)).Take(topK).ToList();
    }
}
