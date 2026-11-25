using System.Net;
using System.Text;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure;
using FlowMind.Infrastructure.AI;
using FlowMind.Infrastructure.Database;
using FlowMind.Infrastructure.Retrieval;
using FlowMind.Infrastructure.VectorStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowMind.Tests;

public class ChunkerTests
{
    [Fact]
    public void Chunks_never_span_pages_and_keep_the_exact_page_number()
    {
        var pages = new[] { (1, "Alpha section describes patient registration in detail."), (2, "Beta section describes billing and invoices in detail.") };
        var chunks = Chunker.Build(pages, 1200, 150);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(new[] { 1, 2 }, chunks.Select(c => c.PageNumber));
        Assert.Contains("registration", chunks[0].Content);
        Assert.DoesNotContain("billing", chunks[0].Content);
        Assert.Equal(new[] { 0, 1 }, chunks.Select(c => c.Index));
    }

    [Fact]
    public void Long_pages_split_within_the_limit_with_overlap_and_lose_no_words()
    {
        var words = Enumerable.Range(0, 600).Select(i => $"word{i}").ToArray();
        var text = string.Join(' ', words);
        var chunks = Chunker.Build(new[] { (3, text) }, 400, 80);

        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => { Assert.Equal(3, c.PageNumber); Assert.True(c.Content.Length <= 400 + 80 + 20); });
        var all = string.Join(' ', chunks.Select(c => c.Content));
        Assert.All(words, w => Assert.Contains(w, all));
        // overlap: the start of chunk n+1 repeats something from the end of chunk n
        var firstWordOfNext = chunks[1].Content.Split(' ')[0];
        Assert.Contains(firstWordOfNext, chunks[0].Content);
    }

    [Fact]
    public void Tiny_or_blank_pages_produce_no_chunks_and_output_is_deterministic()
    {
        Assert.Empty(Chunker.Build(new[] { (1, "   "), (2, "ok") }, 1200, 150));
        var a = Chunker.Build(new[] { (1, "The discharge date is mandatory for every patient.") }, 1200, 150);
        var b = Chunker.Build(new[] { (1, "The discharge date is mandatory for every patient.") }, 1200, 150);
        Assert.Equal(a[0].Hash, b[0].Hash);
    }
}

public class CloudflareEmbeddingProviderTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Code, string Body)> _responses = new();
        public List<(string Url, string? Auth, string Body)> Requests { get; } = new();
        public FakeHandler Enqueue(HttpStatusCode code, string body) { _responses.Enqueue((code, body)); return this; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), await request.Content!.ReadAsStringAsync(ct)));
            var (code, body) = _responses.Dequeue();
            return new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static CloudflareEmbeddingProvider Make(FakeHandler h, string token = "tok", int dims = 3) => new(
        new HttpClient(h),
        Options.Create(new AiProvidersOptions { Cloudflare = new CloudflareOptions { AccountId = "acc1", ApiToken = token, Dimensions = dims } }),
        TimeSpan.Zero);

    private const string TwoVectors = """{"success":true,"errors":[],"result":{"shape":[2,3],"data":[[0.1,0.2,0.3],[0.4,0.5,0.6]]}}""";

    [Fact]
    public async Task Success_returns_vectors_and_uses_bearer_auth_and_text_array_body()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, TwoVectors);
        var v = await Make(h).EmbedAsync(new[] { "a", "b" }, default);

        Assert.Equal(2, v.Count);
        Assert.Equal(0.5f, v[1][1]);
        var req = h.Requests.Single();
        Assert.Contains("accounts/acc1/ai/run/@cf/baai/bge-m3", req.Url);
        Assert.Equal("Bearer tok", req.Auth);
        Assert.Contains("\"text\":[\"a\",\"b\"]", req.Body);
    }

    [Fact]
    public async Task Wrong_dimension_or_count_is_rejected()
    {
        await Assert.ThrowsAsync<InvalidAnalysisOutputException>(() =>
            Make(new FakeHandler().Enqueue(HttpStatusCode.OK, TwoVectors), dims: 1024).EmbedAsync(new[] { "a", "b" }, default));
        await Assert.ThrowsAsync<InvalidAnalysisOutputException>(() =>
            Make(new FakeHandler().Enqueue(HttpStatusCode.OK, TwoVectors)).EmbedAsync(new[] { "only one" }, default));
    }

    [Fact]
    public async Task Missing_credentials_make_no_http_call()
    {
        var h = new FakeHandler();
        await Assert.ThrowsAsync<ProviderNotConfiguredException>(() => Make(h, token: "").EmbedAsync(new[] { "a" }, default));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Daily_allocation_429_is_not_retried()
    {
        var h = new FakeHandler().Enqueue((HttpStatusCode)429, """{"errors":[{"code":4006,"message":"you have used up your daily free allocation of 10,000 neurons"}]}""");
        await Assert.ThrowsAsync<QuotaExceededException>(() => Make(h).EmbedAsync(new[] { "a" }, default));
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Server_errors_retry_then_become_transient()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.BadGateway, "{}").Enqueue(HttpStatusCode.BadGateway, "{}").Enqueue(HttpStatusCode.BadGateway, "{}");
        await Assert.ThrowsAsync<TransientProviderException>(() => Make(h).EmbedAsync(new[] { "a" }, default));
        Assert.Equal(3, h.Requests.Count);
    }
}

public class IndexingTests
{
    private sealed class Unscoped : ITenantContext { public Guid? TenantId => null; }

    private sealed class FakeEmbedder : IEmbeddingProvider
    {
        public int Calls, Texts;
        public string Name => "cloudflare";
        public int Dimensions => 4;
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Calls++; Texts += texts.Count;
            return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[] { 1, 0, 0, 0 }).ToList());
        }
    }

    private sealed class FakeStore : IVectorStore
    {
        public Dictionary<(Guid Version, int Index), ChunkRecord> Rows = new();
        public List<Guid> DeletedDocs = new();
        public Task<Dictionary<int, string>> GetIndexedHashesAsync(Guid t, Guid v, CancellationToken ct) =>
            Task.FromResult(Rows.Where(r => r.Key.Version == v).ToDictionary(r => r.Key.Index, r => r.Value.ContentHash));
        public Task UpsertAsync(Guid t, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct)
        { foreach (var c in chunks) Rows[(c.DocumentVersionId, c.ChunkIndex)] = c; return Task.CompletedTask; }
        public Task DeleteVersionChunksNotInAsync(Guid t, Guid v, IReadOnlyCollection<int> keep, CancellationToken ct)
        { foreach (var k in Rows.Keys.Where(k => k.Version == v && !keep.Contains(k.Index)).ToList()) Rows.Remove(k); return Task.CompletedTask; }
        public Task DeleteOtherVersionsAsync(Guid t, Guid d, Guid keep, CancellationToken ct)
        { foreach (var k in Rows.Where(r => r.Value.DocumentId == d && r.Key.Version != keep).Select(r => r.Key).ToList()) Rows.Remove(k); return Task.CompletedTask; }
        public Task DeleteDocumentAsync(Guid t, Guid d, CancellationToken ct) { DeletedDocs.Add(d); return Task.CompletedTask; }
        public Task DeleteTenantAsync(Guid t, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ChunkHit>> SearchAsync(Guid t, Guid a, string q, float[] e, int k, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChunkHit>>(Array.Empty<ChunkHit>());
    }

    private static readonly Guid Tenant = Guid.NewGuid(), App = Guid.NewGuid();

    private static (DocumentIndexer Indexer, FlowMindDbContext Db, FakeEmbedder Emb, FakeStore Store, Document Doc, DocumentVersion Ver) Setup(
        int neuronLimit, DocumentStatus status = DocumentStatus.Approved)
    {
        var db = new FlowMindDbContext(new DbContextOptionsBuilder<FlowMindDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new Unscoped());
        var doc = new Document { TenantId = Tenant, ApplicationId = App, FileName = "manual.pdf", ContentType = "application/pdf", Status = status };
        var ver = new DocumentVersion { TenantId = Tenant, DocumentId = doc.Id, VersionNumber = 1, ContentHash = "h", StoragePath = "p" };
        doc.CurrentVersionId = ver.Id;
        db.Documents.Add(doc); db.DocumentVersions.Add(ver);
        db.DocumentPages.Add(new DocumentPage { TenantId = Tenant, DocumentVersionId = ver.Id, PageNumber = 1, Text = "Patient registration requires a valid national identifier." });
        db.DocumentPages.Add(new DocumentPage { TenantId = Tenant, DocumentVersionId = ver.Id, PageNumber = 2, Text = "Discharge date is mandatory before the summary can be printed." });
        db.SaveChanges();

        var providers = Options.Create(new AiProvidersOptions { Cloudflare = new CloudflareOptions { DailyNeuronLimit = neuronLimit, Dimensions = 4, BatchSize = 1 } });
        var emb = new FakeEmbedder(); var store = new FakeStore();
        var service = new EmbeddingService(emb, new AiUsageService(db, providers), providers);
        var indexer = new DocumentIndexer(db, store, service, Options.Create(new RetrievalOptions()), NullLogger<DocumentIndexer>.Instance);
        return (indexer, db, emb, store, doc, ver);
    }

    private static DocumentProcessingJob NewJob(FlowMindDbContext db, DocumentVersion v)
    {
        var job = new DocumentProcessingJob { TenantId = Tenant, DocumentVersionId = v.Id, Stage = JobStage.Embedding };
        db.ProcessingJobs.Add(job); db.SaveChanges();
        return job;
    }

    [Fact]
    public async Task Embedding_is_disabled_by_default_no_provider_call_and_job_waits()
    {
        var (indexer, db, emb, store, doc, ver) = Setup(neuronLimit: 0);
        var job = NewJob(db, ver);
        await indexer.ProcessAsync(job, default);

        Assert.Equal(0, emb.Calls);
        Assert.Empty(store.Rows);
        Assert.Equal(JobStatus.QuotaExceeded, job.Status);
        Assert.Equal(DocumentStatus.Approved, doc.Status);               // never un-approved
        Assert.Contains(db.AiUsageLogs, l => l.ProviderKind == AiProviderKind.Embedding && l.Status == AiRequestStatus.QuotaExceeded);
    }

    [Fact]
    public async Task Approved_document_is_chunked_embedded_with_page_citations_and_usage_logged()
    {
        var (indexer, db, emb, store, doc, ver) = Setup(neuronLimit: 10_000);
        var job = NewJob(db, ver);
        await indexer.ProcessAsync(job, default);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(2, store.Rows.Count);
        Assert.Equal(new[] { 1, 2 }, store.Rows.Values.OrderBy(r => r.ChunkIndex).Select(r => r.PageNumber));
        Assert.All(store.Rows.Values, r => { Assert.Equal("manual.pdf", r.DocumentName); Assert.Equal(App, r.ApplicationId); });
        var logs = db.AiUsageLogs.Where(l => l.ProviderKind == AiProviderKind.Embedding && l.Status == AiRequestStatus.Success).ToList();
        Assert.Equal(2, logs.Count);
        Assert.All(logs, l => Assert.True(l.EmbeddingUnits > 0));
    }

    [Fact]
    public async Task Reindexing_unchanged_content_embeds_nothing_again()
    {
        var (indexer, db, emb, store, doc, ver) = Setup(neuronLimit: 10_000);
        await indexer.ProcessAsync(NewJob(db, ver), default);
        var callsAfterFirst = emb.Calls;
        await indexer.ProcessAsync(NewJob(db, ver), default);

        Assert.Equal(callsAfterFirst, emb.Calls);
        Assert.Equal(2, store.Rows.Count);
    }

    [Fact]
    public async Task Quota_stop_midway_keeps_stored_chunks_and_resumes_without_repeating_them()
    {
        // ~14 neurons per chunk batch of 1 (≈20 tokens × 1075/1e6 → rounds up to 1); use a tight cap to stop after the first call.
        var (indexer, db, emb, store, doc, ver) = Setup(neuronLimit: 1);
        var job = NewJob(db, ver);
        await indexer.ProcessAsync(job, default);

        Assert.Equal(JobStatus.QuotaExceeded, job.Status);
        Assert.Single(store.Rows);
        Assert.Equal(1, emb.Calls);

        // After the quota resets (limit raised), only the missing chunk is embedded.
        var providers = Options.Create(new AiProvidersOptions { Cloudflare = new CloudflareOptions { DailyNeuronLimit = 10_000, Dimensions = 4, BatchSize = 1 } });
        var service = new EmbeddingService(emb, new AiUsageService(db, providers), providers);
        var resumed = new DocumentIndexer(db, store, service, Options.Create(new RetrievalOptions()), NullLogger<DocumentIndexer>.Instance);
        await resumed.ProcessAsync(NewJob(db, ver), default);

        Assert.Equal(2, store.Rows.Count);
        Assert.Equal(2, emb.Calls);
    }

    [Fact]
    public async Task Unapproved_documents_are_never_indexed()
    {
        var (indexer, db, emb, store, doc, ver) = Setup(neuronLimit: 10_000, status: DocumentStatus.PendingReview);
        var job = NewJob(db, ver);
        await indexer.ProcessAsync(job, default);

        Assert.Equal(0, emb.Calls);
        Assert.Empty(store.Rows);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Contains("Skipped", job.Error);
    }

    [Fact]
    public async Task Deleted_document_purges_its_vectors_instead_of_indexing()
    {
        var (indexer, db, emb, store, doc, ver) = Setup(neuronLimit: 10_000);
        doc.DeletedUtc = DateTime.UtcNow; db.SaveChanges();
        await indexer.ProcessAsync(NewJob(db, ver), default);

        Assert.Equal(0, emb.Calls);
        Assert.Contains(doc.Id, store.DeletedDocs);
    }

    [Fact]
    public async Task Old_version_chunks_are_removed_once_the_new_version_is_indexed()
    {
        var (indexer, db, emb, store, doc, ver) = Setup(neuronLimit: 10_000);
        var oldVersion = Guid.NewGuid();
        store.Rows[(oldVersion, 0)] = new ChunkRecord(Guid.NewGuid(), App, doc.Id, oldVersion, "manual.pdf", 0, 1, "old text", "x", new float[4]);
        await indexer.ProcessAsync(NewJob(db, ver), default);

        Assert.DoesNotContain(store.Rows.Keys, k => k.Version == oldVersion);
        Assert.Equal(2, store.Rows.Count);
    }
}

public class PgVectorHelpersTests
{
    [Fact]
    public void Keyword_query_is_sanitised_or_joined_and_capped()
    {
        Assert.Equal("what | validations | exist | discharge", PgVectorStore.BuildKeywordQuery("What validations exist? discharge!"));
        Assert.Equal("", PgVectorStore.BuildKeywordQuery("?! () -- ;"));
        var q = PgVectorStore.BuildKeywordQuery("a'b | c:* & !d (e) drop table users");
        Assert.DoesNotContain("'", q); Assert.DoesNotContain("&", q); Assert.DoesNotContain(":", q); Assert.DoesNotContain("(", q);
        Assert.True(PgVectorStore.BuildKeywordQuery(string.Join(' ', Enumerable.Range(0, 50).Select(i => "term" + i))).Split(" | ").Length <= 12);
    }

    [Fact]
    public void Vector_literal_uses_invariant_culture()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("[0.5,-1,0.25]", PgVectorStore.ToVectorLiteral(new[] { 0.5f, -1f, 0.25f }));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }
}

/// <summary>Runs against a real PostgreSQL+pgvector only when FLOWMIND_TEST_PG is set (e.g. the docker-compose database).</summary>
public class PgVectorStoreLiveTests
{
    private static float[] Vec(int hot) { var v = new float[1024]; v[hot] = 1; return v; }

    [Fact]
    public async Task Search_is_tenant_and_application_scoped_hybrid_and_purgeable()
    {
        var cs = Environment.GetEnvironmentVariable("FLOWMIND_TEST_PG");
        if (string.IsNullOrWhiteSpace(cs)) return; // not configured: covered by the manual live run

        var store = new PgVectorStore(cs, Options.Create(new AiProvidersOptions()));
        Guid tA = Guid.NewGuid(), tB = Guid.NewGuid(), app = Guid.NewGuid(), docA = Guid.NewGuid(), docB = Guid.NewGuid(), verA = Guid.NewGuid(), verB = Guid.NewGuid();
        try
        {
            await store.UpsertAsync(tA, new[]
            {
                new ChunkRecord(Guid.NewGuid(), app, docA, verA, "a.pdf", 0, 1, "Discharge date is mandatory for every patient.", "h0", Vec(1)),
                new ChunkRecord(Guid.NewGuid(), app, docA, verA, "a.pdf", 1, 2, "Billing invoices are generated nightly.", "h1", Vec(2)),
            }, default);
            await store.UpsertAsync(tB, new[]
            {
                new ChunkRecord(Guid.NewGuid(), app, docB, verB, "b.pdf", 0, 1, "Discharge secrets of tenant B.", "h2", Vec(1)),
            }, default);

            var hitsA = await store.SearchAsync(tA, app, "discharge date", Vec(1), 5, default);
            Assert.Equal("a.pdf", hitsA[0].DocumentName);
            Assert.Equal(1, hitsA[0].PageNumber);
            Assert.DoesNotContain(hitsA, h => h.DocumentName == "b.pdf");           // other tenant never visible
            Assert.Empty(await store.SearchAsync(tA, Guid.NewGuid(), "discharge", Vec(1), 5, default)); // other application

            var keywordOnly = await store.SearchAsync(tA, app, "invoices", Vec(7), 5, default);       // vector says nothing; keyword finds it
            Assert.Contains(keywordOnly, h => h.PageNumber == 2);

            await store.DeleteDocumentAsync(tA, docA, default);
            Assert.Empty(await store.SearchAsync(tA, app, "discharge", Vec(1), 5, default));
            Assert.NotEmpty(await store.SearchAsync(tB, app, "discharge", Vec(1), 5, default));        // deleting A did not touch B
        }
        finally
        {
            await store.DeleteDocumentAsync(tA, docA, default);
            await store.DeleteDocumentAsync(tB, docB, default);
        }
    }
}
