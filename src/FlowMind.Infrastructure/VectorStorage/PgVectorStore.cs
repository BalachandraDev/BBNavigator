using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FlowMind.Application.Interfaces;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace FlowMind.Infrastructure.VectorStorage;

/// <summary>
/// PostgreSQL + pgvector implementation. All SQL is fixed text with bound parameters (user/AI text is never
/// concatenated into SQL) and every statement filters on tenant_id.
/// </summary>
public sealed class PgVectorStore : IVectorStore
{
    private readonly string _connectionString;
    private readonly int _dimensions;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private volatile bool _schemaReady;

    public PgVectorStore(string connectionString, IOptions<AiProvidersOptions> options)
    {
        _connectionString = connectionString;
        _dimensions = options.Value.Cloudflare.Dimensions;
    }

    internal static string SchemaSql(int dimensions) => $@"
CREATE EXTENSION IF NOT EXISTS vector;
CREATE TABLE IF NOT EXISTS document_chunks (
    id                  uuid        PRIMARY KEY,
    tenant_id           uuid        NOT NULL,
    application_id      uuid        NOT NULL,
    document_id         uuid        NOT NULL,
    document_version_id uuid        NOT NULL,
    document_name       text        NOT NULL,
    chunk_index         integer     NOT NULL,
    page_number         integer     NOT NULL,
    content             text        NOT NULL,
    content_hash        char(64)    NOT NULL,
    embedding           vector({dimensions}) NOT NULL,
    tsv                 tsvector GENERATED ALWAYS AS (to_tsvector('english', content)) STORED,
    created_utc         timestamptz NOT NULL DEFAULT now(),
    UNIQUE (document_version_id, chunk_index)
);
CREATE INDEX IF NOT EXISTS ix_chunks_scope ON document_chunks (tenant_id, application_id);
CREATE INDEX IF NOT EXISTS ix_chunks_doc   ON document_chunks (tenant_id, document_id);
CREATE INDEX IF NOT EXISTS ix_chunks_emb   ON document_chunks USING hnsw (embedding vector_cosine_ops);
CREATE INDEX IF NOT EXISTS ix_chunks_tsv   ON document_chunks USING gin (tsv);";

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        if (!_schemaReady)
        {
            await _schemaGate.WaitAsync(ct);
            try
            {
                if (!_schemaReady)
                {
                    await using var setup = new NpgsqlConnection(_connectionString);
                    await setup.OpenAsync(ct);
                    await using var cmd = new NpgsqlCommand(SchemaSql(_dimensions), setup);
                    await cmd.ExecuteNonQueryAsync(ct);
                    _schemaReady = true;
                }
            }
            finally { _schemaGate.Release(); }
        }
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    public static string ToVectorLiteral(float[] v)
    {
        var sb = new StringBuilder(v.Length * 10).Append('[');
        for (int i = 0; i < v.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(v[i].ToString("R", CultureInfo.InvariantCulture));
        }
        return sb.Append(']').ToString();
    }

    public async Task<Dictionary<int, string>> GetIndexedHashesAsync(Guid tenantId, Guid versionId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT chunk_index, content_hash FROM document_chunks WHERE tenant_id = @t AND document_version_id = @v", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("v", versionId);
        var map = new Dictionary<int, string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetInt32(0)] = r.GetString(1).Trim();
        return map;
    }

    public async Task UpsertAsync(Guid tenantId, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct)
    {
        if (chunks.Count == 0) return;
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var c in chunks)
        {
            await using var cmd = new NpgsqlCommand(@"
INSERT INTO document_chunks (id, tenant_id, application_id, document_id, document_version_id, document_name,
                             chunk_index, page_number, content, content_hash, embedding)
VALUES (@id, @t, @a, @d, @v, @dn, @ci, @pn, @c, @h, CAST(@e AS vector))
ON CONFLICT (document_version_id, chunk_index) DO UPDATE
   SET page_number = EXCLUDED.page_number, content = EXCLUDED.content,
       content_hash = EXCLUDED.content_hash, embedding = EXCLUDED.embedding
   WHERE document_chunks.tenant_id = EXCLUDED.tenant_id", conn, tx);
            cmd.Parameters.AddWithValue("id", c.Id);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("a", c.ApplicationId);
            cmd.Parameters.AddWithValue("d", c.DocumentId);
            cmd.Parameters.AddWithValue("v", c.DocumentVersionId);
            cmd.Parameters.AddWithValue("dn", c.DocumentName);
            cmd.Parameters.AddWithValue("ci", c.ChunkIndex);
            cmd.Parameters.AddWithValue("pn", c.PageNumber);
            cmd.Parameters.AddWithValue("c", c.Content);
            cmd.Parameters.AddWithValue("h", c.ContentHash);
            cmd.Parameters.AddWithValue("e", NpgsqlDbType.Text, ToVectorLiteral(c.Embedding));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task DeleteVersionChunksNotInAsync(Guid tenantId, Guid versionId, IReadOnlyCollection<int> keepIndexes, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM document_chunks WHERE tenant_id = @t AND document_version_id = @v AND NOT (chunk_index = ANY(@keep))", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("v", versionId);
        cmd.Parameters.AddWithValue("keep", NpgsqlDbType.Array | NpgsqlDbType.Integer, keepIndexes.ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteOtherVersionsAsync(Guid tenantId, Guid documentId, Guid keepVersionId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM document_chunks WHERE tenant_id = @t AND document_id = @d AND document_version_id <> @v", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("d", documentId);
        cmd.Parameters.AddWithValue("v", keepVersionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteDocumentAsync(Guid tenantId, Guid documentId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM document_chunks WHERE tenant_id = @t AND document_id = @d", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("d", documentId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteTenantAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM document_chunks WHERE tenant_id = @t", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>OR-joined, letters/digits-only terms so a natural-language question still matches by keyword.</summary>
    internal static string BuildKeywordQuery(string text)
    {
        var terms = Regex.Matches(text ?? "", @"[\p{L}\p{N}]{2,}")
            .Select(m => m.Value.ToLowerInvariant()).Where(w => !w.Any(Chat.Language.IsTamilChar)).Distinct().Take(12).ToList();   // Tamil is searched by meaning (vectors); the English keyword search only gets Latin words
        return string.Join(" | ", terms);
    }

    // Page limit for a restricted audience: a chunk is searched only if its document is fully open (@fulldocs) or its exact (document, page) is listed (@pdocs/@ppages).
    // NULL arrays = no limit. This is part of the WHERE clause of both candidate queries, so a hidden page can never be a candidate, never ranks, and is never read back.
    private const string SearchSql = @"
WITH v AS (
    SELECT id, ROW_NUMBER() OVER (ORDER BY embedding <=> CAST(@q AS vector)) AS rnk
    FROM document_chunks
    WHERE tenant_id = @t AND application_id = @a AND (CAST(@docs AS uuid[]) IS NULL OR document_id = ANY(CAST(@docs AS uuid[])))
      AND (CAST(@fulldocs AS uuid[]) IS NULL OR document_id = ANY(CAST(@fulldocs AS uuid[]))
           OR EXISTS (SELECT 1 FROM unnest(CAST(@pdocs AS uuid[]), CAST(@ppages AS int[])) AS ap(d, p) WHERE ap.d = document_chunks.document_id AND ap.p = document_chunks.page_number))
    ORDER BY embedding <=> CAST(@q AS vector)
    LIMIT @n
),
k AS (
    SELECT c.id, ROW_NUMBER() OVER (ORDER BY ts_rank_cd(c.tsv, tq.query) DESC) AS rnk
    FROM document_chunks c, to_tsquery('english', @kw) AS tq(query)
    WHERE @kw <> '' AND c.tenant_id = @t AND c.application_id = @a AND (CAST(@docs AS uuid[]) IS NULL OR c.document_id = ANY(CAST(@docs AS uuid[]))) AND c.tsv @@ tq.query
      AND (CAST(@fulldocs AS uuid[]) IS NULL OR c.document_id = ANY(CAST(@fulldocs AS uuid[]))
           OR EXISTS (SELECT 1 FROM unnest(CAST(@pdocs AS uuid[]), CAST(@ppages AS int[])) AS ap(d, p) WHERE ap.d = c.document_id AND ap.p = c.page_number))
    ORDER BY ts_rank_cd(c.tsv, tq.query) DESC
    LIMIT @n
)
SELECT c.id, c.document_id, c.document_name, c.page_number, c.content,
       COALESCE(1.0 / (60 + v.rnk), 0) + COALESCE(1.0 / (60 + k.rnk), 0) AS score,
       1 - (c.embedding <=> CAST(@q AS vector)) AS similarity
FROM document_chunks c
LEFT JOIN v ON v.id = c.id
LEFT JOIN k ON k.id = c.id
WHERE c.tenant_id = @t AND (v.id IS NOT NULL OR k.id IS NOT NULL)
ORDER BY score DESC
LIMIT @topk";

    public Task<IReadOnlyList<ChunkHit>> SearchAsync(Guid tenantId, Guid applicationId, string queryText, float[] queryEmbedding, int topK, CancellationToken ct) =>
        SearchInAsync(tenantId, applicationId, queryText, queryEmbedding, topK, null, ct);

    public Task<IReadOnlyList<ChunkHit>> SearchInAsync(Guid tenantId, Guid applicationId, string queryText, float[] queryEmbedding, int topK, IReadOnlyCollection<Guid>? documentIds, CancellationToken ct) =>
        SearchScopedAsync(tenantId, applicationId, queryText, queryEmbedding, topK, documentIds, null, ct);

    public async Task<IReadOnlyList<ChunkHit>> SearchScopedAsync(Guid tenantId, Guid applicationId, string queryText, float[] queryEmbedding, int topK,
        IReadOnlyCollection<Guid>? documentIds, PageAccess? access, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Keep the HNSW index scanning until the tenant/application filter yields enough rows (pgvector ≥ 0.8).
        await using (var set = new NpgsqlCommand("SELECT set_config('hnsw.iterative_scan', 'relaxed_order', true)", conn, tx))
            await set.ExecuteNonQueryAsync(ct);

        await using var cmd = new NpgsqlCommand(SearchSql, conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("a", applicationId);
        cmd.Parameters.AddWithValue("q", NpgsqlDbType.Text, ToVectorLiteral(queryEmbedding));
        cmd.Parameters.AddWithValue("kw", NpgsqlDbType.Text, BuildKeywordQuery(queryText));
        cmd.Parameters.AddWithValue("n", Math.Max(topK * 4, 20));
        cmd.Parameters.AddWithValue("topk", topK);
        cmd.Parameters.Add(new NpgsqlParameter("docs", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = documentIds == null ? DBNull.Value : documentIds.ToArray() });
        // access == null → no page limit (NULL arrays). An empty list is a real limit that matches nothing.
        cmd.Parameters.Add(new NpgsqlParameter("fulldocs", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = access == null ? DBNull.Value : access.FullDocuments.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("pdocs", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = access == null ? DBNull.Value : access.Pages.Select(p => p.DocumentId).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("ppages", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = access == null ? DBNull.Value : access.Pages.Select(p => p.Page).ToArray() });

        var hits = new List<ChunkHit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            hits.Add(new ChunkHit(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetDouble(5), r.GetDouble(6)));
        return hits;
    }
}
