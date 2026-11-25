using FlowMind.Application.Interfaces;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.Retrieval;

/// <summary>Embeds a question and returns the best-matching approved chunks for one tenant + application.</summary>
public sealed class RetrievalService
{
    public const int MaxQueryChars = 1000;

    private readonly EmbeddingService _embeddings;
    private readonly IVectorStore _store;
    private readonly RetrievalOptions _opt;

    public RetrievalService(EmbeddingService embeddings, IVectorStore store, IOptions<RetrievalOptions> opt)
    {
        _embeddings = embeddings; _store = store; _opt = opt.Value;
    }

    public async Task<IReadOnlyList<ChunkHit>> SearchAsync(Guid tenantId, Guid applicationId, string query, int? topK, CancellationToken ct, IReadOnlyCollection<Guid>? documentIds = null, PageAccess? access = null)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return Array.Empty<ChunkHit>();
        if (query.Length > MaxQueryChars) query = query[..MaxQueryChars];
        var k = Math.Clamp(topK ?? _opt.DefaultTopK, 1, _opt.MaxTopK);

        var vectors = await _embeddings.EmbedAsync(tenantId, new[] { query }, "retrieval-query", ct);
        // access = the pages this caller's audience may see (null = no limit). Applied inside the store's query, not after the fact.
        return await _store.SearchScopedAsync(tenantId, applicationId, query, vectors[0], k, documentIds, access, ct);
    }
}
