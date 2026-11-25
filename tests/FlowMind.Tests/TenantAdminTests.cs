using FlowMind.Infrastructure.Security;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlowMind.API.Authentication;
using FlowMind.API.Tenants;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Database;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlowMind.Tests;

public class SecretBoxTests
{
    private const string Key = "a-server-secret-that-is-long-enough-123";

    [Fact]
    public void Round_trips_and_uses_a_fresh_nonce_each_time()
    {
        var box = new SecretBox(Key);
        var a = box.Encrypt("fm_secret-company-key"); var b = box.Encrypt("fm_secret-company-key");
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("fm_secret", a);
        Assert.Equal("fm_secret-company-key", box.TryDecrypt(a));
        Assert.Equal("fm_secret-company-key", box.TryDecrypt(b));
    }

    [Fact]
    public void Tampering_a_different_server_secret_or_garbage_all_fail_safely()
    {
        var box = new SecretBox(Key);
        var token = box.Encrypt("fm_secret");
        var raw = Convert.FromBase64String(token[3..]); raw[^1] ^= 1;
        Assert.Null(box.TryDecrypt("v1." + Convert.ToBase64String(raw)));              // modified ciphertext
        Assert.Null(new SecretBox("another-server-secret-that-is-long-enough-1").TryDecrypt(token));   // database copied to another server
        Assert.Null(box.TryDecrypt("v1.not-base64!!"));
        Assert.Null(box.TryDecrypt("plain text"));
        Assert.Null(box.TryDecrypt(null));
    }

    [Fact]
    public void Without_a_long_enough_secret_nothing_recoverable_can_be_stored()
    {
        var box = new SecretBox("short");
        Assert.False(box.Enabled);
        Assert.Throws<InvalidOperationException>(() => box.Encrypt("x"));
        Assert.Null(box.TryDecrypt(new SecretBox(Key).Encrypt("x")));
    }
}

public class TenantAdminServiceTests
{
    private sealed class Unscoped : ITenantContext { public Guid? TenantId => null; }
    private sealed class FakeVectors : IVectorStore
    {
        public List<Guid> DeletedTenants = new(); public bool Fail;
        public Task<Dictionary<int, string>> GetIndexedHashesAsync(Guid t, Guid v, CancellationToken ct) => Task.FromResult(new Dictionary<int, string>());
        public Task UpsertAsync(Guid t, IReadOnlyList<ChunkRecord> c, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteVersionChunksNotInAsync(Guid t, Guid v, IReadOnlyCollection<int> k, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteOtherVersionsAsync(Guid t, Guid d, Guid k, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteDocumentAsync(Guid t, Guid d, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteTenantAsync(Guid t, CancellationToken ct) { if (Fail) throw new InvalidOperationException("vector store down"); DeletedTenants.Add(t); return Task.CompletedTask; }
        public Task<IReadOnlyList<ChunkHit>> SearchAsync(Guid t, Guid a, string q, float[] e, int k, CancellationToken ct) => Task.FromResult<IReadOnlyList<ChunkHit>>(Array.Empty<ChunkHit>());
    }
    private sealed class FakeFiles : IFileStorage
    {
        public List<Guid> DeletedTenants = new();
        public Task<StagedFile> StageAsync(Stream c, long m, CancellationToken ct) => throw new NotSupportedException();
        public Task<StoredFile> CommitAsync(StagedFile s, Guid t, Guid d, int v, string e, CancellationToken ct) => throw new NotSupportedException();
        public void Discard(StagedFile s) { }
        public Task<Stream> OpenReadAsync(string p, string k, Guid t, CancellationToken ct) => throw new NotSupportedException();
        public Task<StoredFile> CopyAsync(string p, string f, string to, Guid t, string sha, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(string p, string k, Guid t, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteDocumentAsync(Guid t, Guid d, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteTenantAsync(Guid t, CancellationToken ct) { DeletedTenants.Add(t); return Task.CompletedTask; }
        public Task DeleteVersionAsync(Guid t, Guid d, int v, CancellationToken ct) => Task.CompletedTask;
    }    private sealed class FakeAudit : IAuditLogger
    {
        public List<string> Actions = new();
        public Task LogAsync(string action, string? entityType = null, string? entityId = null, string? detail = null, Guid? tenantId = null, CancellationToken ct = default)
        { Actions.Add(action); return Task.CompletedTask; }
    }

    private static (TenantAdminService Svc, FlowMindDbContext Db, FakeVectors Vec, FakeFiles Files, FakeAudit Audit) Make(string? secrets = "a-server-secret-that-is-long-enough-123")
    {
        var db = new FlowMindDbContext(new DbContextOptionsBuilder<FlowMindDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new Unscoped());
        var vec = new FakeVectors(); var files = new FakeFiles(); var audit = new FakeAudit();
        return (new TenantAdminService(db, new SecretBox(secrets), vec, files, audit, NullLogger<TenantAdminService>.Instance), db, vec, files, audit);
    }

    /// <summary>One row in every table a company owns.</summary>
    private static void SeedEverything(FlowMindDbContext db, Guid t)
    {
        var app = new TenantApplication { TenantId = t, Name = "App" }; db.Applications.Add(app);
        var doc = new Document { TenantId = t, ApplicationId = app.Id, FileName = "a.pdf", ContentType = "application/pdf" };
        var ver = new DocumentVersion { TenantId = t, DocumentId = doc.Id, VersionNumber = 1, ContentHash = "h" + t, StoragePath = "p" };
        var deleted = new Document { TenantId = t, ApplicationId = app.Id, FileName = "old.pdf", ContentType = "application/pdf", DeletedUtc = DateTime.UtcNow };   // soft-deleted rows must go too
        db.Documents.AddRange(doc, deleted); db.DocumentVersions.Add(ver);
        db.DocumentPages.Add(new DocumentPage { TenantId = t, DocumentVersionId = ver.Id, PageNumber = 1, Text = "text" });
        db.ProcessingJobs.Add(new DocumentProcessingJob { TenantId = t, DocumentVersionId = ver.Id });
        var mod = new Module { TenantId = t, ApplicationId = app.Id, Name = "M", NormalizedName = "m" }; db.Modules.Add(mod);
        var page = new AppPage { TenantId = t, ApplicationId = app.Id, Name = "P", NormalizedName = "p" }; db.AppPages.Add(page);
        db.PageFunctionalities.Add(new PageFunctionality { TenantId = t, ApplicationId = app.Id, AppPageId = page.Id, Description = "f" });
        db.ValidationRules.Add(new ValidationRule { TenantId = t, ApplicationId = app.Id, AppPageId = page.Id, RuleText = "r" });
        db.BusinessRules.Add(new BusinessRule { TenantId = t, ApplicationId = app.Id, Statement = "b" });
        db.RolePermissions.Add(new RolePermission { TenantId = t, ApplicationId = app.Id, AppPageId = page.Id, Role = "r", Permission = "p" });
        var wf = new Workflow { TenantId = t, ApplicationId = app.Id, Name = "W", NormalizedName = "w" }; db.Workflows.Add(wf);
        var s1 = new WorkflowStep { TenantId = t, ApplicationId = app.Id, WorkflowId = wf.Id, Name = "S1", NormalizedName = "s1", Order = 1 };
        var s2 = new WorkflowStep { TenantId = t, ApplicationId = app.Id, WorkflowId = wf.Id, Name = "S2", NormalizedName = "s2", Order = 2 };
        db.WorkflowSteps.AddRange(s1, s2);
        db.WorkflowTransitions.Add(new WorkflowTransition { TenantId = t, ApplicationId = app.Id, WorkflowId = wf.Id, FromStepId = s1.Id, ToStepId = s2.Id });
        db.PageLinks.Add(new PageLink { TenantId = t, ApplicationId = app.Id, FromPageId = page.Id, ToPageId = page.Id });
        db.AnalysisCache.Add(new AnalysisCacheEntry { Key = "k" + t.ToString("N"), TenantId = t, Provider = "p", Model = "m", PromptVersion = "v", ResponseJson = "{}" });
        var convo = new Conversation { TenantId = t, ApplicationId = app.Id, OwnerId = "u", Title = "t" }; db.Conversations.Add(convo);
        db.ConversationMessages.Add(new ConversationMessage { TenantId = t, ConversationId = convo.Id, Role = "user", Content = "q" });
        db.ChatCache.Add(new ChatCacheEntry { Key = "c" + t.ToString("N"), TenantId = t, Answer = "a" });
        db.AccessKeys.Add(new TenantAccessKey { TenantId = t, Name = "k", KeyHash = "h" + t.ToString("N") });
        db.AiUsageLogs.Add(new AIUsageLog { TenantId = t, Provider = "groq", Model = "m", ProviderKind = AiProviderKind.Chat, Status = AiRequestStatus.Success, InputTokens = 100 });
        db.SaveChanges();
    }

    private static int Rows(FlowMindDbContext db, Guid t) =>
        db.Applications.IgnoreQueryFilters().Count(x => x.TenantId == t) + db.Documents.IgnoreQueryFilters().Count(x => x.TenantId == t)
        + db.DocumentVersions.IgnoreQueryFilters().Count(x => x.TenantId == t) + db.DocumentPages.IgnoreQueryFilters().Count(x => x.TenantId == t)
        + db.ProcessingJobs.IgnoreQueryFilters().Count(x => x.TenantId == t) + db.Modules.IgnoreQueryFilters().Count(x => x.TenantId == t)
        + db.AppPages.IgnoreQueryFilters().Count(x => x.TenantId == t) + db.PageFunctionalities.IgnoreQueryFilters().Count(x => x.TenantId == t)
        + db.ValidationRules.IgnoreQueryFilters().Count(x => x.TenantId == t) + db.BusinessRules.IgnoreQueryFilters().Count(x => x.TenantId == t)
        + db.RolePermissions.IgnoreQueryFilters().Count(x => x.TenantId == t) + db.Workflows.IgnoreQueryFilters().Count(x => x.TenantId == t)
        + db.WorkflowSteps.IgnoreQueryFilters().Count(x => x.TenantId == t) + db.WorkflowTransitions.IgnoreQueryFilters().Count(x => x.TenantId == t)
        + db.PageLinks.IgnoreQueryFilters().Count(x => x.TenantId == t) + db.AnalysisCache.IgnoreQueryFilters().Count(x => x.TenantId == t)
        + db.Conversations.IgnoreQueryFilters().Count(x => x.TenantId == t) + db.ConversationMessages.IgnoreQueryFilters().Count(x => x.TenantId == t)
        + db.ChatCache.IgnoreQueryFilters().Count(x => x.TenantId == t) + db.AccessKeys.IgnoreQueryFilters().Count(x => x.TenantId == t)
        + db.Tenants.Count(x => x.Id == t);

    [Fact]
    public async Task A_new_company_has_a_working_key_that_the_admin_can_view_again()
    {
        var (svc, db, _, _, audit) = Make();
        var (tenant, key) = await svc.CreateAsync("Acme", "acme", default);

        Assert.True(ApiKeys.Verify(key, tenant.ApiKeyHash));                         // sign-in still verifies the hash
        Assert.DoesNotContain(key, tenant.ApiKeyCipher);                             // what is stored is not the key
        var login = await svc.RevealAsync(tenant.Id, default);
        Assert.Equal(("acme", key), (login.CompanyId, login.Key));
        Assert.Contains("tenant.key.reveal", audit.Actions);                         // viewing is audited
        Assert.True((await svc.ListAsync(default)).Single().KeyViewable);
        _ = db;
    }

    [Fact]
    public async Task Companies_created_before_key_storage_cannot_be_revealed_but_can_be_reset()
    {
        var (svc, db, _, _, _) = Make();
        var old = new Tenant { Name = "Old", Slug = "old", ApiKeyHash = ApiKeys.Hash("fm_old-key-xxxxxxxxxxxx"), ApiKeyPrefix = "fm_old-k" };   // no cipher
        db.Tenants.Add(old); db.SaveChanges();

        var ex = await Assert.ThrowsAsync<TenantAdminException>(() => svc.RevealAsync(old.Id, default));
        Assert.Equal(409, ex.Status);
        Assert.Contains("Reset key", ex.Message);

        var fresh = await svc.ResetKeyAsync(old.Id, default);
        Assert.Equal(fresh.Key, (await svc.RevealAsync(old.Id, default)).Key);
    }

    [Fact]
    public async Task Without_a_server_secret_the_key_is_never_stored_and_cannot_be_revealed()
    {
        var (svc, _, _, _, _) = Make(secrets: null);
        var (tenant, key) = await svc.CreateAsync("Acme", "acme", default);
        Assert.Null(tenant.ApiKeyCipher);
        Assert.True(ApiKeys.Verify(key, tenant.ApiKeyHash));
        await Assert.ThrowsAsync<TenantAdminException>(() => svc.RevealAsync(tenant.Id, default));
    }

    [Fact]
    public async Task Resetting_the_key_kills_the_old_one_and_ends_older_sessions()
    {
        var (svc, db, _, _, audit) = Make();
        var (tenant, oldKey) = await svc.CreateAsync("Acme", "acme", default);

        var fresh = await svc.ResetKeyAsync(tenant.Id, default);

        var t = db.Tenants.Single();
        Assert.NotEqual(oldKey, fresh.Key);
        Assert.False(ApiKeys.Verify(oldKey, t.ApiKeyHash));
        Assert.True(ApiKeys.Verify(fresh.Key, t.ApiKeyHash));
        Assert.NotNull(t.TokensValidAfterUtc);
        Assert.Contains("tenant.key.reset", audit.Actions);
    }

    [Fact]
    public async Task Disabling_and_enabling_access_flips_the_flag_and_is_audited()
    {
        var (svc, db, _, _, audit) = Make();
        var (tenant, _) = await svc.CreateAsync("Acme", "acme", default);
        await svc.SetActiveAsync(tenant.Id, false, default);
        Assert.False(db.Tenants.Single().IsActive);
        await svc.SetActiveAsync(tenant.Id, true, default);
        Assert.True(db.Tenants.Single().IsActive);
        Assert.Equal(new[] { "tenant.create", "tenant.disable", "tenant.enable" }, audit.Actions.ToArray());
    }

    [Fact]
    public async Task Delete_needs_the_exact_company_id_and_removes_nothing_otherwise()
    {
        var (svc, db, vec, files, _) = Make();
        var (tenant, _) = await svc.CreateAsync("Acme", "acme", default);
        SeedEverything(db, tenant.Id);
        var before = Rows(db, tenant.Id);

        foreach (var wrong in new[] { null, "", "ACME", "acme ", "other" })
        {
            var ex = await Assert.ThrowsAsync<TenantAdminException>(() => svc.DeleteAsync(tenant.Id, wrong, default));
            Assert.Equal(400, ex.Status);
        }
        Assert.Equal(before, Rows(db, tenant.Id));
        Assert.Empty(vec.DeletedTenants); Assert.Empty(files.DeletedTenants);
        Assert.Equal(404, (await Assert.ThrowsAsync<TenantAdminException>(() => svc.DeleteAsync(Guid.NewGuid(), "acme", default))).Status);
    }

    [Fact]
    public async Task Delete_removes_everything_of_that_company_and_nothing_of_any_other()
    {
        var (svc, db, vec, files, audit) = Make();
        var (a, _) = await svc.CreateAsync("Acme", "acme", default);
        var (b, _) = await svc.CreateAsync("Beta", "beta", default);
        SeedEverything(db, a.Id); SeedEverything(db, b.Id);
        var betaBefore = Rows(db, b.Id);
        Assert.True(Rows(db, a.Id) > 20);

        await svc.DeleteAsync(a.Id, "acme", default);

        Assert.Equal(0, Rows(db, a.Id));                                    // every table, including the soft-deleted document and the tenant itself
        Assert.Equal(betaBefore, Rows(db, b.Id));                           // the other company is untouched
        Assert.Equal(new[] { a.Id }, vec.DeletedTenants.ToArray());         // search vectors purged
        Assert.Equal(new[] { a.Id }, files.DeletedTenants.ToArray());       // stored files purged
        Assert.Contains("tenant.delete", audit.Actions);
        Assert.Equal(100, db.AiUsageLogs.Where(l => l.TenantId == a.Id).Sum(l => l.InputTokens));   // usage counters are kept: they account for the shared provider limits
    }

    [Fact]
    public async Task If_the_search_index_cannot_be_cleared_nothing_is_half_deleted_and_the_company_stays_locked_out()
    {
        var (svc, db, vec, files, _) = Make();
        var (a, _) = await svc.CreateAsync("Acme", "acme", default);
        SeedEverything(db, a.Id);
        var before = Rows(db, a.Id);
        vec.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteAsync(a.Id, "acme", default));

        Assert.Equal(before, Rows(db, a.Id));                               // database rows still there: retry is possible
        Assert.False(db.Tenants.Single().IsActive);                         // but nobody can use the company meanwhile
        Assert.Empty(files.DeletedTenants);

        vec.Fail = false;
        await svc.DeleteAsync(a.Id, "acme", default);                       // the retry completes the job
        Assert.Equal(0, Rows(db, a.Id));
    }

    [Fact]
    public async Task The_list_shows_counts_and_status_per_company()
    {
        var (svc, db, _, _, _) = Make();
        var (a, _) = await svc.CreateAsync("Acme", "acme", default);
        await svc.CreateAsync("Beta", "beta", default);
        SeedEverything(db, a.Id);
        await svc.SetActiveAsync(a.Id, false, default);

        var rows = await svc.ListAsync(default);
        var acme = rows.Single(r => r.Slug == "acme"); var beta = rows.Single(r => r.Slug == "beta");
        Assert.Equal((1, 1, 1, false), (acme.Applications, acme.Documents, acme.UserKeys, acme.IsActive));   // the soft-deleted document is not counted
        Assert.Equal((0, 0, 0, true), (beta.Applications, beta.Documents, beta.UserKeys, beta.IsActive));
    }
}

public class SessionCutoffTests
{
    private sealed class Unscoped : ITenantContext { public Guid? TenantId => null; }
    private static System.Security.Claims.ClaimsPrincipal Admin(Guid tenant, DateTime issuedUtc) => new(new System.Security.Claims.ClaimsIdentity(new[]
    {
        new System.Security.Claims.Claim("role", Roles.TenantAdmin), new System.Security.Claims.Claim("tid", tenant.ToString()),
        new System.Security.Claims.Claim("sub", "tenant:t"), new System.Security.Claims.Claim("iat", new DateTimeOffset(issuedUtc).ToUnixTimeSeconds().ToString()),
    }, "test"));

    [Fact]
    public async Task Company_admin_sessions_issued_before_a_key_reset_are_rejected_and_newer_ones_are_not()
    {
        using var db = new FlowMindDbContext(new DbContextOptionsBuilder<FlowMindDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new Unscoped());
        var t = new Tenant { Name = "A", Slug = "a", TokensValidAfterUtc = new DateTime(2026, 11, 7, 10, 0, 0, 500, DateTimeKind.Utc) };
        db.Tenants.Add(t); db.SaveChanges();
        Task<bool> Valid(DateTime issued) => new TokenStatusChecker(new MemoryCache(new MemoryCacheOptions())).IsStillValidAsync(Admin(t.Id, issued), db, default);

        Assert.False(await Valid(new DateTime(2026, 11, 7, 9, 59, 59, DateTimeKind.Utc)));    // before the reset
        Assert.True(await Valid(new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc)));      // same second as the reset: the new key's own session must work
        Assert.True(await Valid(new DateTime(2026, 11, 7, 10, 5, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task A_token_with_no_issue_time_is_rejected_once_a_cut_off_exists()
    {
        using var db = new FlowMindDbContext(new DbContextOptionsBuilder<FlowMindDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new Unscoped());
        var t = new Tenant { Name = "A", Slug = "a", TokensValidAfterUtc = DateTime.UtcNow }; db.Tenants.Add(t); db.SaveChanges();
        var noIat = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        { new System.Security.Claims.Claim("role", Roles.TenantAdmin), new System.Security.Claims.Claim("tid", t.Id.ToString()), new System.Security.Claims.Claim("sub", "tenant:t") }, "test"));
        Assert.False(await new TokenStatusChecker(new MemoryCache(new MemoryCacheOptions())).IsStillValidAsync(noIat, db, default));
    }
}

public class TenantAdminHttpTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class FakeVectors : IVectorStore
    {
        public List<Guid> DeletedTenants = new();
        public Task<Dictionary<int, string>> GetIndexedHashesAsync(Guid t, Guid v, CancellationToken ct) => Task.FromResult(new Dictionary<int, string>());
        public Task UpsertAsync(Guid t, IReadOnlyList<ChunkRecord> c, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteVersionChunksNotInAsync(Guid t, Guid v, IReadOnlyCollection<int> k, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteOtherVersionsAsync(Guid t, Guid d, Guid k, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteDocumentAsync(Guid t, Guid d, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteTenantAsync(Guid t, CancellationToken ct) { DeletedTenants.Add(t); return Task.CompletedTask; }
        public Task<IReadOnlyList<ChunkHit>> SearchAsync(Guid t, Guid a, string q, float[] e, int k, CancellationToken ct) => Task.FromResult<IReadOnlyList<ChunkHit>>(Array.Empty<ChunkHit>());
    }

    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeVectors _vectors = new();
    public TenantAdminHttpTests(WebApplicationFactory<Program> factory) => _factory = factory.Configure().WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        foreach (var d in s.Where(x => x.ServiceType == typeof(IVectorStore)).ToList()) s.Remove(d);
        s.AddSingleton<IVectorStore>(_vectors);
    }));

    private HttpClient Platform()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Issue("platform-admin", Roles.PlatformAdmin, null).Token);
        return c;
    }

    private static async Task<HttpStatusCode> SignIn(HttpClient c, string slug, string key) =>
        (await c.PostAsJsonAsync("/api/v1/auth/token", new { tenantSlug = slug, apiKey = key })).StatusCode;

    [Fact]
    public async Task The_platform_admin_can_see_a_company_s_login_and_manage_its_access_end_to_end()
    {
        var admin = Platform();
        var slug = "e2e-" + Guid.NewGuid().ToString("N")[..8];

        var created = await (await admin.PostAsJsonAsync("/api/v1/tenants", new { name = "E2E Co", slug })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid(); var key = created.GetProperty("apiKey").GetString()!;
        var anon = _factory.CreateClient();

        // listed, and the login can be viewed again
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/v1/tenants");
        Assert.Contains(list.EnumerateArray(), r => r.GetProperty("slug").GetString() == slug && r.GetProperty("keyViewable").GetBoolean());
        var shown = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/tenants/{id}/key");
        Assert.Equal((slug, key), (shown.GetProperty("companyId").GetString(), shown.GetProperty("key").GetString()));
        Assert.Equal(HttpStatusCode.OK, await SignIn(anon, slug, key));

        // disable: sign-in stops at once; enable: it works again
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/tenants/{id}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await SignIn(anon, slug, key));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/tenants/{id}/enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await SignIn(anon, slug, key));

        // reset: the old key stops working, the new one is what the admin now sees
        var reset = await (await admin.PostAsync($"/api/v1/tenants/{id}/reset-key", null)).Content.ReadFromJsonAsync<JsonElement>();
        var newKey = reset.GetProperty("key").GetString()!;
        Assert.NotEqual(key, newKey);
        Assert.Equal(HttpStatusCode.Unauthorized, await SignIn(anon, slug, key));
        Assert.Equal(HttpStatusCode.OK, await SignIn(anon, slug, newKey));
        Assert.Equal(newKey, (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/tenants/{id}/key")).GetProperty("key").GetString());

        // delete: wrong confirmation is refused; the right one removes the company completely
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/v1/tenants/{id}?confirm=wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/v1/tenants/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/tenants/{id}?confirm={slug}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await SignIn(anon, slug, newKey));
        Assert.DoesNotContain((await admin.GetFromJsonAsync<JsonElement>("/api/v1/tenants")).EnumerateArray(), r => r.GetProperty("slug").GetString() == slug);
        Assert.Contains(id, _vectors.DeletedTenants);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/v1/tenants/{id}?confirm={slug}")).StatusCode);
    }

    [Fact]
    public async Task A_disabled_company_s_open_session_stops_working()
    {
        var admin = Platform();
        var slug = "sess-" + Guid.NewGuid().ToString("N")[..8];
        var created = await (await admin.PostAsJsonAsync("/api/v1/tenants", new { name = "Session Co", slug })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        var token = (await (await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/token", new { tenantSlug = slug, apiKey = created.GetProperty("apiKey").GetString() })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        var company = _factory.CreateClient(); company.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await admin.PostAsync($"/api/v1/tenants/{id}/disable", null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await company.GetAsync("/api/v1/applications")).StatusCode);   // already-issued token, refused
    }

    [Fact]
    public async Task A_key_reset_ends_the_company_admin_s_open_session()
    {
        var admin = Platform();
        var slug = "reset-" + Guid.NewGuid().ToString("N")[..8];
        var created = await (await admin.PostAsJsonAsync("/api/v1/tenants", new { name = "Reset Co", slug })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        var token = (await (await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/token", new { tenantSlug = slug, apiKey = created.GetProperty("apiKey").GetString() })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        var company = _factory.CreateClient(); company.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await Task.Delay(1100);                                          // token times have 1-second resolution
        await admin.PostAsync($"/api/v1/tenants/{id}/reset-key", null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await company.GetAsync("/api/v1/applications")).StatusCode);
    }
}
