using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlowMind.API.Authentication;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Access;
using FlowMind.Infrastructure.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowMind.Tests;

/// <summary>
/// Audiences and access rules, end to end: one module document is split between Manager, Nurse and Ward, and every way of reading it
/// (chat, the one-call API, search, the knowledge pages, share links) must follow the split. The model is shown only what the audience may see.
/// </summary>
public class AccessRulesTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string AdminText = "CONTROLPANEL-SECRET: only managers configure the hospital here.";
    private const string NurseText = "NURSE-WORKLIST: nurses see their patients and tasks here.";
    private const string WardText = "WARD-BEDBOARD: ward staff manage beds and transfers here.";
    private const string IntroText = "INTRO-GENERAL: this guide explains the whole module.";

    private sealed class FakeEmbedder : IEmbeddingProvider
    {
        public string Name => "cloudflare"; public int Dimensions => 4;
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> t, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<float[]>>(t.Select(_ => new float[] { 1, 0, 0, 0 }).ToList());
    }
    /// <summary>Remembers everything the model was shown, so a test can prove hidden text never reached it.</summary>
    private sealed class FakeChat : IChatProvider
    {
        public readonly ConcurrentQueue<string> Prompts = new();
        public string Name => "groq";
        public Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessageDto> m, int max, CancellationToken ct)
        {
            Prompts.Enqueue(string.Join("\n", m.Select(x => x.Content)));
            return Task.FromResult(new ChatCompletionResult("The page explains it. [1]", 50, 10));
        }
        public string Last => Prompts.Last();
    }
    /// <summary>A store that "finds" the same four pages for every question, best first, and applies no filter of its own except what the interface's default gives.</summary>
    private sealed class FakeStore : IVectorStore
    {
        public readonly ConcurrentDictionary<Guid, ChunkHit[]> ByTenant = new();
        public Task<Dictionary<int, string>> GetIndexedHashesAsync(Guid t, Guid v, CancellationToken ct) => Task.FromResult(new Dictionary<int, string>());
        public Task UpsertAsync(Guid t, IReadOnlyList<ChunkRecord> c, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteVersionChunksNotInAsync(Guid t, Guid v, IReadOnlyCollection<int> k, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteOtherVersionsAsync(Guid t, Guid d, Guid k, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteDocumentAsync(Guid t, Guid d, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteTenantAsync(Guid t, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ChunkHit>> SearchAsync(Guid t, Guid a, string q, float[] e, int k, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChunkHit>>(ByTenant.GetValueOrDefault(t) ?? Array.Empty<ChunkHit>());
    }

    private readonly WebApplicationFactory<Program> _f;
    private readonly FakeChat _chat = new();
    private readonly FakeStore _store = new();

    public AccessRulesTests(WebApplicationFactory<Program> factory) => _f = factory.Configure().WithWebHostBuilder(b =>
    {
        b.UseSetting("AiProviders:Groq:DailyRequestLimit", "10000");
        b.UseSetting("AiProviders:Cloudflare:DailyNeuronLimit", "1000000");
        b.ConfigureServices(s =>
        {
            foreach (var d in s.Where(x => x.ServiceType == typeof(IEmbeddingProvider) || x.ServiceType == typeof(IChatProvider) || x.ServiceType == typeof(IVectorStore)).ToList()) s.Remove(d);
            s.AddSingleton<IEmbeddingProvider, FakeEmbedder>(); s.AddSingleton<IChatProvider>(_chat); s.AddSingleton<IVectorStore>(_store);
        });
    });

    /// <summary>One approved 4-page document split into three modules. Page 1 is the introduction no module claims.</summary>
    private sealed record World(Guid Tenant, string Slug, Guid App, Guid Doc, Guid ModAdmin, Guid ModNurse, Guid ModWard, Guid PgControl, Guid PgWorklist, Guid PgBed, Guid WfAdmin, Guid WfNurse);

    private World Seed()
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
        var t = Guid.NewGuid();
        var slug = "acc-" + t.ToString("N")[..8];
        db.Tenants.Add(new Tenant { Id = t, Name = "Acme Hospital", Slug = slug, ApiKeyHash = ApiKeys.Hash(ApiKeys.Generate()) });
        var app = new TenantApplication { TenantId = t, Name = "HMIS" }; db.Applications.Add(app);
        var version = Guid.NewGuid();
        var doc = new Document { TenantId = t, ApplicationId = app.Id, FileName = "guide.pdf", ContentType = "application/pdf", Status = DocumentStatus.Approved, CurrentVersionId = version };
        db.Documents.Add(doc);
        db.DocumentVersions.Add(new DocumentVersion { Id = version, TenantId = t, DocumentId = doc.Id, VersionNumber = 1, ContentHash = "h", StoragePath = "x" });
        foreach (var n in new[] { 1, 2, 3, 4 }) db.DocumentPages.Add(new DocumentPage { TenantId = t, DocumentVersionId = version, PageNumber = n, Text = "page " + n });

        Module Mod(string name, int page) => new() { TenantId = t, ApplicationId = app.Id, Name = name, NormalizedName = name.ToLowerInvariant(), Review = ReviewStatus.Approved, SourceDocumentVersionId = version, SourcePage = page };
        AppPage Pg(string name, Guid mod, int page) => new() { TenantId = t, ApplicationId = app.Id, ModuleId = mod, Name = name, NormalizedName = name.ToLowerInvariant(), Review = ReviewStatus.Approved, SourceDocumentVersionId = version, SourcePage = page };
        var mAdmin = Mod("Administration", 2); var mNurse = Mod("Nursing", 3); var mWard = Mod("Ward", 4);
        db.Modules.AddRange(mAdmin, mNurse, mWard);
        var pControl = Pg("Control panel", mAdmin.Id, 2); var pWork = Pg("Nurse worklist", mNurse.Id, 3); var pBed = Pg("Bed board", mWard.Id, 4);
        db.AppPages.AddRange(pControl, pWork, pBed);

        Workflow Wf(string name, Guid mod, AppPage page, int srcPage)
        {
            var w = new Workflow { TenantId = t, ApplicationId = app.Id, ModuleId = mod, Name = name, NormalizedName = name.ToLowerInvariant(), Review = ReviewStatus.Approved, SourceDocumentVersionId = version, SourcePage = srcPage };
            db.Workflows.Add(w);
            db.WorkflowSteps.Add(new WorkflowStep { TenantId = t, ApplicationId = app.Id, WorkflowId = w.Id, Order = 1, Name = "Open " + page.Name, NormalizedName = "open", AppPageId = page.Id, Review = ReviewStatus.Approved, SourceDocumentVersionId = version, SourcePage = srcPage });
            return w;
        }
        var wfAdmin = Wf("Configure the hospital", mAdmin.Id, pControl, 2); var wfNurse = Wf("Start a nursing round", mNurse.Id, pWork, 3);
        db.SaveChanges();

        ChunkHit Hit(int page, string text, double score) => new(Guid.NewGuid(), doc.Id, "guide.pdf", page, text, score, 0.9);
        _store.ByTenant[t] = new[] { Hit(2, AdminText, 0.04), Hit(3, NurseText, 0.03), Hit(4, WardText, 0.02), Hit(1, IntroText, 0.01) };
        return new World(t, slug, app.Id, doc.Id, mAdmin.Id, mNurse.Id, mWard.Id, pControl.Id, pWork.Id, pBed.Id, wfAdmin.Id, wfNurse.Id);
    }

    private HttpClient Admin(Guid tenant)
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _f.Services.GetRequiredService<TokenService>().Issue("tenant:t", Roles.TenantAdmin, tenant).Token);
        return c;
    }
    /// <summary>A user signed in with a key, through the real token exchange.</summary>
    private async Task<HttpClient> UserJwt(World w, string key)
    {
        var res = await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/token", new { tenantSlug = w.Slug, apiKey = key });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        return c;
    }
    private HttpClient ApiKey(string key) { var c = _f.CreateClient(); c.DefaultRequestHeaders.Add("X-Api-Key", key); return c; }
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    /// <param name="browse">Most tests exercise what the Knowledge page shows, so their audiences may open it; the chat-only default has its own test.</param>
    private static async Task<Guid> NewAudience(HttpClient admin, string name, string? aliases = null, bool browse = true)
    {
        var res = await admin.PostAsJsonAsync("/api/v1/access/audiences", new { name, aliases, canBrowseKnowledge = browse });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await Json(res)).GetProperty("id").GetGuid();
    }
    private static async Task<(Guid Id, string Key)> NewKey(HttpClient admin, Guid? audience, string name = "user")
    {
        var res = await admin.PostAsJsonAsync("/api/v1/access-keys", new { name, audienceId = audience });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var j = await Json(res);
        return (j.GetProperty("id").GetGuid(), j.GetProperty("key").GetString()!);
    }
    private static object Rule(string kind, Guid id, params Guid[] audiences) => new { kind, targetId = id, mode = "audiences", audienceIds = audiences };
    private static object Everyone(string kind, Guid id) => new { kind, targetId = id, mode = "everyone", audienceIds = Array.Empty<Guid>() };
    private static async Task<HttpResponseMessage> Save(HttpClient admin, params object[] changes) => await admin.PutAsJsonAsync("/api/v1/access/rules", new { changes });

    private sealed record Setup(World W, HttpClient Admin, Guid Manager, Guid Nurse, Guid Ward, string ManagerKey, string NurseKey, string WardKey, string FullKey);

    /// <summary>Manager sees everything; Nurse the nursing module and the introduction; Ward the ward module and the introduction.</summary>
    private async Task<Setup> Split()
    {
        var w = Seed(); var admin = Admin(w.Tenant);
        var m = await NewAudience(admin, "Manager"); var n = await NewAudience(admin, "Nurse"); var wd = await NewAudience(admin, "Ward");
        var res = await Save(admin,
            Rule("module", w.ModAdmin, m), Rule("page", w.PgControl, m),
            Rule("module", w.ModNurse, m, n), Rule("page", w.PgWorklist, m, n),
            Rule("module", w.ModWard, m, wd), Rule("page", w.PgBed, m, wd),
            Everyone("document", w.Doc));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return new Setup(w, admin, m, n, wd, (await NewKey(admin, m, "managers")).Key, (await NewKey(admin, n, "nurses")).Key, (await NewKey(admin, wd, "ward")).Key, (await NewKey(admin, null, "everything")).Key);
    }

    private int _q;
    private async Task<JsonElement> Ask(string key) => await Json(await ApiKey(key).PostAsJsonAsync("/api/ask", new { message = $"How does this part of the system work number {++_q}?" }));
    private static int FirstPage(JsonElement ask) => ask.GetProperty("sources")[0].GetProperty("page").GetInt32();

    // ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Each_audience_is_answered_only_from_its_own_pages_and_the_model_never_sees_the_rest()
    {
        var s = await Split();

        var manager = await Ask(s.ManagerKey);
        Assert.Equal(2, FirstPage(manager));
        Assert.Contains("CONTROLPANEL-SECRET", _chat.Last);

        var nurse = await Ask(s.NurseKey);
        Assert.Equal(3, FirstPage(nurse));
        Assert.Contains("NURSE-WORKLIST", _chat.Last); Assert.Contains("INTRO-GENERAL", _chat.Last);
        Assert.DoesNotContain("CONTROLPANEL-SECRET", _chat.Last); Assert.DoesNotContain("WARD-BEDBOARD", _chat.Last);

        var ward = await Ask(s.WardKey);
        Assert.Equal(4, FirstPage(ward));
        Assert.Contains("WARD-BEDBOARD", _chat.Last);
        Assert.DoesNotContain("CONTROLPANEL-SECRET", _chat.Last); Assert.DoesNotContain("NURSE-WORKLIST", _chat.Last);

        var full = await Ask(s.FullKey);   // a key with no audience keeps seeing everything, as before audiences existed
        Assert.Equal(2, FirstPage(full));
        Assert.Contains("NURSE-WORKLIST", _chat.Last); Assert.Contains("WARD-BEDBOARD", _chat.Last);
    }

    [Fact]
    public async Task The_signed_in_chat_follows_the_same_split_and_the_company_admin_sees_everything()
    {
        var s = await Split();
        var nurse = await UserJwt(s.W, s.NurseKey);
        var res = await nurse.PostAsJsonAsync("/api/v1/chat", new { applicationId = s.W.App, message = "What does the first screen do for me exactly?" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(3, (await Json(res)).GetProperty("sources")[0].GetProperty("page").GetInt32());
        Assert.DoesNotContain("CONTROLPANEL-SECRET", _chat.Last);

        var admin = await s.Admin.PostAsJsonAsync("/api/v1/chat", new { applicationId = s.W.App, message = "Show me the very first screen of all please." });
        Assert.Equal(2, (await Json(admin)).GetProperty("sources")[0].GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task Search_and_the_knowledge_pages_hide_what_the_audience_may_not_see()
    {
        var s = await Split();
        var nurse = await UserJwt(s.W, s.NurseKey);

        var hits = await Json(await nurse.PostAsJsonAsync("/api/v1/search", new { applicationId = s.W.App, query = "configure the hospital" }));
        var pages = hits.EnumerateArray().Select(h => h.GetProperty("page").GetInt32()).OrderBy(p => p).ToList();
        Assert.Equal(new[] { 1, 3 }, pages);
        Assert.DoesNotContain("CONTROLPANEL-SECRET", hits.ToString());

        var mods = await Json(await nurse.GetAsync($"/api/v1/modules?applicationId={s.W.App}"));
        var names = mods.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "Nursing" }, names);

        Assert.Equal(HttpStatusCode.OK, (await nurse.GetAsync($"/api/v1/pages/{s.W.PgWorklist}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await nurse.GetAsync($"/api/v1/pages/{s.W.PgControl}")).StatusCode);   // looks exactly like a page that does not exist
        Assert.Equal(HttpStatusCode.NotFound, (await nurse.GetAsync($"/api/v1/pages/{s.W.PgBed}")).StatusCode);

        var wfs = await Json(await nurse.GetAsync($"/api/v1/workflows?applicationId={s.W.App}"));
        Assert.Equal(new[] { "Start a nursing round" }, wfs.EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await nurse.GetAsync($"/api/v1/workflows/{s.W.WfAdmin}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await nurse.GetAsync($"/api/v1/workflows/{s.W.WfNurse}")).StatusCode);

        var manager = await UserJwt(s.W, s.ManagerKey);
        var all = await Json(await manager.GetAsync($"/api/v1/modules?applicationId={s.W.App}"));
        Assert.Equal(3, all.GetProperty("modules").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/v1/pages/{s.W.PgControl}")).StatusCode);
    }

    [Fact]
    public async Task Content_nobody_has_decided_on_is_closed_to_audiences_but_not_to_keys_without_one()
    {
        var s = await Split();
        var visitor = await NewAudience(s.Admin, "Visitor");            // no ticks at all
        var key = (await NewKey(s.Admin, visitor, "visitors")).Key;

        var ask = await Ask(key);
        Assert.Equal(1, FirstPage(ask));                                  // only the document leftover (page 1) was opened to everyone
        Assert.DoesNotContain("NURSE-WORKLIST", _chat.Last); Assert.DoesNotContain("CONTROLPANEL-SECRET", _chat.Last);
        var mods = await Json(await (await UserJwt(s.W, key)).GetAsync($"/api/v1/modules?applicationId={s.W.App}"));
        Assert.Equal(0, mods.GetProperty("modules").GetArrayLength());

        // A page added later (no rule yet) stays closed to the Nurse audience until someone decides, yet full-access keys still see it.
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
            var version = db.Documents.IgnoreQueryFilters().Single(d => d.Id == s.W.Doc).CurrentVersionId;
            db.AppPages.Add(new AppPage { TenantId = s.W.Tenant, ApplicationId = s.W.App, ModuleId = s.W.ModNurse, Name = "Shift handover", NormalizedName = "shift handover", Review = ReviewStatus.Approved, SourceDocumentVersionId = version, SourcePage = 3 });
            db.SaveChanges();
        }
        AccessResolver.Invalidate(s.W.Tenant);
        var nurse = await UserJwt(s.W, s.NurseKey);
        var pagesNurse = await Json(await nurse.GetAsync($"/api/v1/modules?applicationId={s.W.App}"));
        Assert.DoesNotContain("Shift handover", pagesNurse.ToString());
        Assert.Contains("Shift handover", (await Json(await (await UserJwt(s.W, s.FullKey)).GetAsync($"/api/v1/modules?applicationId={s.W.App}"))).ToString());
        // ...and because it cites page 3 together with the Nurse worklist, that page now needs both decided: the stricter rule wins.
        var nurseAsk = await Ask(s.NurseKey);
        Assert.NotEqual(3, FirstPage(nurseAsk));
    }

    [Fact]
    public async Task A_share_link_with_an_audience_answers_only_from_what_that_audience_may_see()
    {
        var s = await Split();
        var create = await s.Admin.PostAsJsonAsync("/api/v1/bot-links", new { name = "Nurses station", title = "Nurse help", audienceId = s.Nurse });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var link = await Json(create);
        Assert.Equal("Nurse", link.GetProperty("audienceName").GetString());
        var token = link.GetProperty("token").GetString()!;
        var linkId = link.GetProperty("id").GetGuid();

        HttpClient Visitor() { var c = _f.CreateClient(); c.DefaultRequestHeaders.Add("X-Bot-Token", token); c.DefaultRequestHeaders.Add("X-Bot-Visitor", "visitor-0001"); return c; }
        var res = await Visitor().PostAsJsonAsync("/api/bot/ask", new { message = "Tell me about the control panel settings please." });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("NURSE-WORKLIST", _chat.Last);
        Assert.DoesNotContain("CONTROLPANEL-SECRET", _chat.Last); Assert.DoesNotContain("WARD-BEDBOARD", _chat.Last);

        // Removing the audience gives the link everything it is mapped to again.
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PatchAsJsonAsync($"/api/v1/bot-links/{linkId}", new { noAudience = true })).StatusCode);
        await Visitor().PostAsJsonAsync("/api/bot/ask", new { message = "Now tell me about the control panel again please." });
        Assert.Contains("CONTROLPANEL-SECRET", _chat.Last);

        var other = Seed();
        var foreign = await NewAudience(Admin(other.Tenant), "Other company group");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PostAsJsonAsync("/api/v1/bot-links", new { name = "X", title = "Y", audienceId = foreign })).StatusCode);
    }

    [Fact]
    public async Task Changing_a_key_audience_or_a_rule_takes_effect_for_that_key()
    {
        var s = await Split();
        var (keyId, key) = await NewKey(s.Admin, s.Nurse, "temp nurse");
        Assert.Equal(3, FirstPage(await Ask(key)));

        // Move the key to Ward.
        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.PatchAsJsonAsync($"/api/v1/access-keys/{keyId}", new { audienceId = s.Ward })).StatusCode);
        Assert.Equal(4, FirstPage(await Ask(key)));

        // Open the control panel to Ward as well.
        Assert.Equal(HttpStatusCode.OK, (await Save(s.Admin, Rule("module", s.W.ModAdmin, s.Manager, s.Ward), Rule("page", s.W.PgControl, s.Manager, s.Ward))).StatusCode);
        Assert.Equal(2, FirstPage(await Ask(key)));

        // Back to full access.
        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.PatchAsJsonAsync($"/api/v1/access-keys/{keyId}", new { fullAccess = true })).StatusCode);
        Assert.Equal(2, FirstPage(await Ask(key)));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PatchAsJsonAsync($"/api/v1/access-keys/{keyId}", new { })).StatusCode);
    }

    [Fact]
    public async Task Asking_what_can_I_ask_lists_only_the_topics_that_person_may_see_without_calling_an_AI()
    {
        var s = await Split();
        async Task<string> Topics(string key, string q)
        {
            var before = _chat.Prompts.Count;
            var j = await Json(await ApiKey(key).PostAsJsonAsync("/api/ask", new { message = q }));
            Assert.Equal(before, _chat.Prompts.Count);                       // answered from stored knowledge: no model call
            Assert.True(j.GetProperty("documented").GetBoolean());            // never the "not documented" card
            return j.GetProperty("answer").GetString()!;
        }

        var nurse = await Topics(s.NurseKey, "what can I ask?");
        Assert.Contains("Nursing", nurse); Assert.Contains("Nurse worklist", nurse);
        foreach (var hidden in new[] { "Administration", "Control panel", "Ward", "Bed board" }) Assert.DoesNotContain(hidden, nurse);

        var ward = await Topics(s.WardKey, "what data can be shown");
        Assert.Contains("Bed board", ward); Assert.DoesNotContain("Nurse worklist", ward); Assert.DoesNotContain("Control panel", ward);

        var manager = await Topics(s.ManagerKey, "what approved for me to search");
        foreach (var shown in new[] { "Administration", "Control panel", "Nursing", "Nurse worklist", "Ward", "Bed board" }) Assert.Contains(shown, manager);
        var full = await Topics(s.FullKey, "list the modules");
        Assert.Contains("Control panel", full); Assert.Contains("Bed board", full);

        // An audience that may see nothing is told so, plainly.
        var none = (await NewKey(s.Admin, await NewAudience(s.Admin, "Visitor"), "visitors")).Key;
        Assert.Contains("Nothing has been approved", await Topics(none, "what can I see"));

        // A real question about a page still goes to the documents (and so to the model).
        var before = _chat.Prompts.Count;
        await ApiKey(s.NurseKey).PostAsJsonAsync("/api/ask", new { message = "what can I see on the nurse worklist page" });
        Assert.Equal(before + 1, _chat.Prompts.Count);
    }

    [Fact]
    public async Task An_audience_is_chat_only_unless_its_admin_switches_the_Knowledge_page_on()
    {
        var w = Seed(); var admin = Admin(w.Tenant);
        var chatOnly = await NewAudience(admin, "Chat only", browse: false);
        Assert.Equal(HttpStatusCode.OK, (await Save(admin, Everyone("module", w.ModWard), Everyone("page", w.PgBed), Everyone("document", w.Doc))).StatusCode);
        var key = (await NewKey(admin, chatOnly, "kiosk")).Key;
        var full = (await NewKey(admin, null, "everything")).Key;
        var user = await UserJwt(w, key);

        // what the app asks, and what the server enforces
        Assert.False((await Json(await user.GetAsync("/api/v1/me/access"))).GetProperty("canBrowseKnowledge").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync($"/api/v1/modules?applicationId={w.App}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync($"/api/v1/pages/{w.PgBed}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync($"/api/v1/workflows?applicationId={w.App}")).StatusCode);
        // the chat still works for them
        Assert.Equal(HttpStatusCode.OK, (await ApiKey(key).PostAsJsonAsync("/api/ask", new { message = "How does the bed board work?" })).StatusCode);

        // a key with no audience keeps both
        var fullUser = await UserJwt(w, full);
        Assert.True((await Json(await fullUser.GetAsync("/api/v1/me/access"))).GetProperty("canBrowseKnowledge").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await fullUser.GetAsync($"/api/v1/modules?applicationId={w.App}")).StatusCode);
        // and the company admin is never blocked
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/modules?applicationId={w.App}")).StatusCode);

        // switching it on opens the page, still limited to what the rules allow
        var aud = (await Json(await admin.GetAsync("/api/v1/access/audiences"))).EnumerateArray().Single(a => a.GetProperty("name").GetString() == "Chat only");
        Assert.False(aud.GetProperty("canBrowseKnowledge").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/v1/access/audiences/" + chatOnly, new { name = "Chat only", canBrowseKnowledge = true })).StatusCode);
        var modules = await Json(await (await UserJwt(w, key)).GetAsync($"/api/v1/modules?applicationId={w.App}"));
        var names = modules.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();
        Assert.Contains("Ward", names); Assert.DoesNotContain("Administration", names);
    }

    [Fact]
    public async Task The_rules_page_says_which_links_and_keys_have_no_audience_so_the_rules_do_not_apply_to_them()
    {
        var s = await Split();                                                  // creates three audience keys and one full-access key ("everything")
        Assert.Equal(HttpStatusCode.Created, (await s.Admin.PostAsJsonAsync("/api/v1/bot-links", new { name = "Open link", title = "Help" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await s.Admin.PostAsJsonAsync("/api/v1/bot-links", new { name = "Ward link", title = "Ward help", audienceId = s.Ward })).StatusCode);

        var ov = await Json(await s.Admin.GetAsync($"/api/v1/access/overview?applicationId={s.W.App}"));
        Assert.Equal(new[] { "Open link" }, ov.GetProperty("openLinks").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(new[] { "everything" }, ov.GetProperty("openKeys").EnumerateArray().Select(x => x.GetString()).ToArray());

        // Giving the open link an audience clears it from the list.
        var links = await Json(await s.Admin.GetAsync("/api/v1/bot-links"));
        var open = links.EnumerateArray().Single(l => l.GetProperty("name").GetString() == "Open link").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PatchAsJsonAsync("/api/v1/bot-links/" + open, new { audienceId = s.Ward })).StatusCode);
        ov = await Json(await s.Admin.GetAsync($"/api/v1/access/overview?applicationId={s.W.App}"));
        Assert.Equal(0, ov.GetProperty("openLinks").GetArrayLength());
    }

    [Fact]
    public async Task The_unrelated_reply_names_the_document_and_only_the_topics_that_person_may_ask_about()
    {
        var s = await Split();
        async Task<string> Reply(string key)
        {
            var before = _chat.Prompts.Count;
            var j = await Json(await ApiKey(key).PostAsJsonAsync("/api/ask", new { message = "tell me a joke" }));
            Assert.Equal(before, _chat.Prompts.Count);                        // no model call
            return j.GetProperty("answer").GetString()!;
        }

        var nurse = await Reply(s.NurseKey);
        Assert.Contains("outside what I can help with", nurse);
        Assert.Contains("Nursing", nurse); Assert.Contains("module", nurse);
        foreach (var hidden in new[] { "Administration", "Ward", "guide.pdf" }) Assert.DoesNotContain(hidden, nurse);

        var manager = await Reply(s.ManagerKey);
        foreach (var shown in new[] { "Administration", "Nursing", "Ward" }) Assert.Contains(shown, manager);
        Assert.DoesNotContain("guide.pdf", manager);                          // file names are never shown
    }

    [Theory]
    [InlineData("Sure, here are the questions that are listed in the BMW module documentation, each with a source citation:", "Sure, here are the questions that are listed in the BMW module:")]
    [InlineData("According to the sources, the next step is Review.", "The next step is Review.")]
    [InlineData("Based on the provided documents, a nurse can edit the counts.", "A nurse can edit the counts.")]
    [InlineData("This is described in the documentation.", "This is described in the system.")]
    [InlineData("Upload the patient documents on the Admission page.", "Upload the patient documents on the Admission page.")]
    [InlineData("Attach a file to the bill, then save it.", "Attach a file to the bill, then save it.")]
    public void Answers_never_point_at_where_the_knowledge_is_stored(string input, string expected) =>
        Assert.Equal(expected, FlowMind.Infrastructure.Chat.AnswerFormatter.Clean(input));

    [Fact]
    public void Fixed_replies_never_mention_documents_sources_or_pdfs()
    {
        var all = new[]
        {
            FlowMind.Infrastructure.Chat.ChatService.NotDocumentedAnswer,
            FlowMind.Infrastructure.Chat.SmallTalk.OffTopic("BackBone"), FlowMind.Infrastructure.Chat.SmallTalk.Unrelated("BackBone"),
            FlowMind.Infrastructure.Chat.SmallTalk.Unrelated("BackBone", "en", new[] { "BMW" }), FlowMind.Infrastructure.Chat.SmallTalk.TryReply("hello", "BackBone")!,
            FlowMind.Infrastructure.Chat.SmallTalk.TryReply("who are you", "BackBone")!,
        };
        foreach (var text in all)
            foreach (var word in new[] { "document", "source", "pdf", "file" })
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_unrelated_reply_names_the_modules_it_answers_about()
    {
        static string R(params string[] m) => FlowMind.Infrastructure.Chat.SmallTalk.Unrelated("BackBone", "en", m);
        Assert.Contains("questions about the BMW module.", R("BMW"));
        Assert.Contains("the BMW and Ward modules.", R("BMW", "Ward"));
        Assert.Contains("the A, B and C modules.", R("A", "B", "C"));
        var many = R("M1", "M2", "M3", "M4", "M5", "M6", "M7");
        Assert.Contains("M5, and 2 more modules", many); Assert.DoesNotContain("M6", many);
        Assert.Contains("BackBone", R());                                            // no modules approved yet: names the application instead
        Assert.Contains("what can I ask?", FlowMind.Infrastructure.Chat.SmallTalk.Unrelated("BackBone", "ta", new[] { "BMW" }));
    }

    [Fact]
    public async Task A_share_link_lists_only_topics_from_its_documents_and_audience()
    {
        var s = await Split();
        var link = await Json(await s.Admin.PostAsJsonAsync("/api/v1/bot-links", new { name = "Ward kiosk", title = "Ward help", audienceId = s.Ward }));
        var c = _f.CreateClient(); c.DefaultRequestHeaders.Add("X-Bot-Token", link.GetProperty("token").GetString()); c.DefaultRequestHeaders.Add("X-Bot-Visitor", "visitor-0002");
        var answer = (await Json(await c.PostAsJsonAsync("/api/bot/ask", new { message = "what can I ask?" }))).GetProperty("answer").GetString()!;
        Assert.Contains("Bed board", answer); Assert.DoesNotContain("Control panel", answer); Assert.DoesNotContain("Nurse worklist", answer);
    }

    [Fact]
    public async Task A_key_can_be_copied_again_by_its_own_company_admin_only_and_never_after_revoking()
    {
        var w = Seed(); var admin = Admin(w.Tenant);
        var (id, key) = await NewKey(admin, null, "ward team");

        var list = await Json(await admin.GetAsync("/api/v1/access-keys"));
        Assert.True(list.EnumerateArray().Single().GetProperty("canCopy").GetBoolean());
        var shown = await admin.GetAsync($"/api/v1/access-keys/{id}/key");
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.Contains("no-store", shown.Headers.CacheControl?.ToString());
        Assert.Equal(key, (await Json(shown)).GetProperty("key").GetString());          // the same key, and it still works
        await UserJwt(w, key);

        using (var scope = _f.Services.CreateScope())   // stored encrypted, never as typed
        {
            var cipher = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>().AccessKeys.IgnoreQueryFilters().Single(k => k.Id == id).KeyCipher;
            Assert.StartsWith("v1.", cipher); Assert.DoesNotContain(key, cipher);
        }

        var other = Admin(Seed().Tenant);                                                // another company cannot reveal it
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/access-keys/{id}/key")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await UserJwt(w, key)).GetAsync($"/api/v1/access-keys/{id}/key")).StatusCode);   // and a user cannot reveal keys
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync($"/api/v1/access-keys/{id}/key")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/access-keys/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/access-keys/{id}/key")).StatusCode);     // revoked: gone for good
        Assert.False((await Json(await admin.GetAsync("/api/v1/access-keys"))).EnumerateArray().Single().GetProperty("canCopy").GetBoolean());

        // A key made before copying existed has no stored copy: it is listed but cannot be shown.
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
            var old = ApiKeys.Generate();
            db.AccessKeys.Add(new TenantAccessKey { TenantId = w.Tenant, Name = "old", KeyPrefix = old[..8], KeyHash = ApiKeys.Hash(old) });
            db.SaveChanges();
        }
        var oldRow = (await Json(await admin.GetAsync("/api/v1/access-keys"))).EnumerateArray().Single(k => k.GetProperty("name").GetString() == "old");
        Assert.False(oldRow.GetProperty("canCopy").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/access-keys/{oldRow.GetProperty("id").GetGuid()}/key")).StatusCode);
    }

    [Fact]
    public async Task A_lost_key_is_replaced_in_one_step_keeping_name_and_audience_and_the_old_one_stops_working()
    {
        var s = await Split();
        var (oldId, oldKey) = await NewKey(s.Admin, s.Nurse, "night nurses");
        // an old-style key (no stored copy) can be replaced too
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
            db.AccessKeys.IgnoreQueryFilters().Single(k => k.Id == oldId).KeyCipher = null; db.SaveChanges();
        }
        Assert.Equal(3, FirstPage(await Ask(oldKey)));

        var res = await s.Admin.PostAsJsonAsync($"/api/v1/access-keys/{oldId}/replace", new { });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var fresh = await Json(res);
        var newKey = fresh.GetProperty("key").GetString()!;
        Assert.NotEqual(oldKey, newKey);
        Assert.Equal("night nurses", fresh.GetProperty("name").GetString()); Assert.Equal("Nurse", fresh.GetProperty("audienceName").GetString());

        Assert.Equal(3, FirstPage(await Ask(newKey)));                                                        // same audience, so same pages
        Assert.Equal(HttpStatusCode.Unauthorized, (await ApiKey(oldKey).PostAsJsonAsync("/api/ask", new { message = "Is this old key still accepted?" })).StatusCode);
        var rows = (await Json(await s.Admin.GetAsync("/api/v1/access-keys"))).EnumerateArray().Where(k => k.GetProperty("name").GetString() == "night nurses").ToList();
        Assert.Equal(2, rows.Count); Assert.Single(rows, k => k.GetProperty("active").GetBoolean() && k.GetProperty("canCopy").GetBoolean());
        Assert.Equal(newKey, (await Json(await s.Admin.GetAsync($"/api/v1/access-keys/{fresh.GetProperty("id").GetGuid()}/key"))).GetProperty("key").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.PostAsJsonAsync($"/api/v1/access-keys/{oldId}/replace", new { })).StatusCode);   // already revoked
        Assert.Equal(HttpStatusCode.NotFound, (await Admin(Seed().Tenant).PostAsJsonAsync($"/api/v1/access-keys/{fresh.GetProperty("id").GetGuid()}/replace", new { })).StatusCode);   // another company
        var user = await UserJwt(s.W, newKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync($"/api/v1/access-keys/{fresh.GetProperty("id").GetGuid()}/replace", new { })).StatusCode);
    }

    [Fact]
    public async Task Audience_management_is_validated_and_never_leaves_a_key_with_a_dangling_audience()
    {
        var w = Seed(); var admin = Admin(w.Tenant);
        var nurse = await NewAudience(admin, "Nurse");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/access/audiences", new { name = "nurse" })).StatusCode);   // names are unique, ignoring case in the key lookup below
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/access/audiences", new { name = "x" })).StatusCode);

        var (keyId, _) = await NewKey(admin, nurse);
        var del = await admin.DeleteAsync($"/api/v1/access/audiences/{nurse}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
        Assert.Contains("1 key", await del.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PatchAsJsonAsync($"/api/v1/access-keys/{keyId}", new { fullAccess = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/access/audiences/{nurse}")).StatusCode);
        Assert.Equal(0, (await Json(await admin.GetAsync("/api/v1/access/audiences"))).GetArrayLength());

        // A revoked key is detached, so the audience can go; the key stays revoked.
        var again = await NewAudience(admin, "Nurse");
        var (revokedId, _) = await NewKey(admin, again, "old");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/access-keys/{revokedId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/access/audiences/{again}")).StatusCode);
    }

    [Fact]
    public async Task One_company_cannot_use_or_see_another_companys_audiences_and_rules()
    {
        var a = Seed(); var b = Seed();
        var adminA = Admin(a.Tenant); var adminB = Admin(b.Tenant);
        var aud = await NewAudience(adminA, "Nurse");

        Assert.Equal(0, (await Json(await adminB.GetAsync("/api/v1/access/audiences"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"/api/v1/access/audiences/{aud}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminB.PostAsJsonAsync("/api/v1/access-keys", new { name = "k", audienceId = aud })).StatusCode);

        // B tries to write a rule for A's module, and a rule that names A's audience on its own module.
        var bad1 = await Save(adminB, Rule("module", a.ModNurse, aud));
        var bad2 = await Save(adminB, Rule("module", b.ModNurse, aud));
        Assert.Equal(HttpStatusCode.BadRequest, bad1.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad2.StatusCode);
        using var scope = _f.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<FlowMindDbContext>().AccessRules.IgnoreQueryFilters().Where(r => r.TenantId == a.Tenant || r.TenantId == b.Tenant));   // all or nothing: nothing was saved
    }

    [Fact]
    public async Task Only_company_admins_can_manage_audiences_and_rules()
    {
        var s = await Split();
        var nurse = await UserJwt(s.W, s.NurseKey);
        foreach (var url in new[] { "/api/v1/access/audiences", $"/api/v1/access/overview?applicationId={s.W.App}", "/api/v1/access/roles-found", $"/api/v1/access/suggestions?applicationId={s.W.App}" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await nurse.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync(url)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.PutAsJsonAsync("/api/v1/access/rules", new { changes = new[] { Everyone("document", s.W.Doc) } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.PostAsJsonAsync("/api/v1/access/audiences", new { name = "Sneaky" })).StatusCode);
    }

    [Fact]
    public async Task The_overview_and_preview_describe_exactly_what_chat_will_do()
    {
        var s = await Split();
        var ov = await Json(await s.Admin.GetAsync($"/api/v1/access/overview?applicationId={s.W.App}"));
        Assert.Equal(3, ov.GetProperty("modules").GetArrayLength());
        Assert.Equal(7, ov.GetProperty("decisions").GetInt32());       // 3 modules + 3 pages + the unclaimed introduction
        Assert.Equal(0, ov.GetProperty("undecided").GetInt32());
        var doc = ov.GetProperty("documents")[0];
        Assert.Equal(1, doc.GetProperty("otherPages").GetInt32()); Assert.Equal("1", doc.GetProperty("otherPageList").GetString());
        var nursing = ov.GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "Nursing");
        Assert.Equal("p. 3", nursing.GetProperty("pages")[0].GetProperty("docPages").GetString());

        var p = await Json(await s.Admin.GetAsync($"/api/v1/access/preview?audienceId={s.Nurse}&applicationId={s.W.App}"));
        Assert.Equal(1, p.GetProperty("openPages").GetInt32()); Assert.Equal(3, p.GetProperty("totalPages").GetInt32());
        Assert.Equal(2, p.GetProperty("openDocumentPages").GetInt32()); Assert.Equal(4, p.GetProperty("totalDocumentPages").GetInt32());   // the introduction and page 3
        Assert.Equal(1, p.GetProperty("openWorkflows").GetInt32()); Assert.Equal(2, p.GetProperty("totalWorkflows").GetInt32());

        // Clearing a decision makes it "not decided" again, and the preview follows.
        Assert.Equal(HttpStatusCode.OK, (await Save(s.Admin, new { kind = "page", targetId = s.W.PgWorklist, mode = "clear", audienceIds = Array.Empty<Guid>() })).StatusCode);
        var after = await Json(await s.Admin.GetAsync($"/api/v1/access/overview?applicationId={s.W.App}"));
        Assert.Equal(1, after.GetProperty("undecided").GetInt32());
        var p2 = await Json(await s.Admin.GetAsync($"/api/v1/access/preview?audienceId={s.Nurse}&applicationId={s.W.App}"));
        Assert.Equal(0, p2.GetProperty("openPages").GetInt32());
    }

    [Fact]
    public async Task Suggestions_read_the_roles_already_extracted_and_save_nothing()
    {
        var w = Seed(); var admin = Admin(w.Tenant);
        var manager = await NewAudience(admin, "Manager"); var nurse = await NewAudience(admin, "Nurse", "sister, charge nurse");
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
            RolePermission R(Guid page, string role, string perm) => new() { TenantId = w.Tenant, ApplicationId = w.App, AppPageId = page, Role = role, Permission = perm, Review = ReviewStatus.Approved };
            db.RolePermissions.AddRange(R(w.PgControl, "Manager", "Full access to every setting"), R(w.PgControl, "Nurse", "No access"),
                R(w.PgWorklist, "Charge Nurse", "Can view and edit"), R(w.PgWorklist, "Manager", "Can view"), R(w.PgBed, "Pharmacist", "Can view"));
            db.SaveChanges();
        }
        var sug = await Json(await admin.GetAsync($"/api/v1/access/suggestions?applicationId={w.App}"));
        var pages = sug.GetProperty("pages");
        Assert.Equal(new[] { manager }, pages.GetProperty(w.PgControl.ToString()).GetProperty("audienceIds").EnumerateArray().Select(x => x.GetGuid()).ToArray());   // Nurse was told "No access"
        Assert.Equal(new[] { manager, nurse }.OrderBy(x => x), pages.GetProperty(w.PgWorklist.ToString()).GetProperty("audienceIds").EnumerateArray().Select(x => x.GetGuid()).OrderBy(x => x));   // alias "Charge Nurse"
        Assert.False(pages.TryGetProperty(w.PgBed.ToString(), out _));   // a role nobody is called: nothing to suggest
        Assert.Contains("Pharmacist", sug.GetProperty("unmatchedRoles").EnumerateArray().Select(x => x.GetString()));

        var found = await Json(await admin.GetAsync("/api/v1/access/roles-found"));
        Assert.Contains("Pharmacist", found.EnumerateArray().Select(x => x.GetProperty("role").GetString()));
        Assert.DoesNotContain("Manager", found.EnumerateArray().Select(x => x.GetProperty("role").GetString()));

        using var check = _f.Services.CreateScope();
        Assert.Empty(check.ServiceProvider.GetRequiredService<FlowMindDbContext>().AccessRules.IgnoreQueryFilters().Where(r => r.TenantId == w.Tenant));   // suggestions are never applied by themselves
    }
}

/// <summary>The decision logic on its own: which document pages and items an audience gets from a set of rules.</summary>
public class AccessLogicTests
{
    private sealed class Unscoped : ITenantContext { public Guid? TenantId => null; }
    private static FlowMindDbContext NewDb() => new(new DbContextOptionsBuilder<FlowMindDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new Unscoped());

    private readonly Guid _t = Guid.NewGuid(), _app = Guid.NewGuid(), _doc = Guid.NewGuid(), _ver = Guid.NewGuid();
    private readonly Guid _nurse = Guid.NewGuid(), _ward = Guid.NewGuid();

    private FlowMindDbContext Db(int pages = 4)
    {
        var db = NewDb();
        db.Tenants.Add(new Tenant { Id = _t, Name = "T", Slug = "t-" + _t.ToString("N")[..6], ApiKeyHash = "x" });
        db.Documents.Add(new Document { Id = _doc, TenantId = _t, ApplicationId = _app, FileName = "d.pdf", Status = DocumentStatus.Approved, CurrentVersionId = _ver });
        db.DocumentVersions.Add(new DocumentVersion { Id = _ver, TenantId = _t, DocumentId = _doc, VersionNumber = 1, ContentHash = "h", StoragePath = "p" });
        for (int i = 1; i <= pages; i++) db.DocumentPages.Add(new DocumentPage { TenantId = _t, DocumentVersionId = _ver, PageNumber = i, Text = "p" + i });
        db.Audiences.AddRange(new Audience { Id = _nurse, TenantId = _t, Name = "Nurse" }, new Audience { Id = _ward, TenantId = _t, Name = "Ward" });
        return db;
    }
    private AppPage Page(FlowMindDbContext db, string name, int srcPage, ReviewStatus review = ReviewStatus.Approved, Guid? module = null)
    {
        var p = new AppPage { TenantId = _t, ApplicationId = _app, ModuleId = module, Name = name, NormalizedName = name, Review = review, SourceDocumentVersionId = _ver, SourcePage = srcPage };
        db.AppPages.Add(p); return p;
    }
    private AccessRule Allow(FlowMindDbContext db, string kind, Guid target, params Guid[] audiences)
    {
        var r = new AccessRule { TenantId = _t, TargetKind = kind, TargetId = target };
        db.AccessRules.Add(r);
        foreach (var a in audiences) db.AccessRuleAudiences.Add(new AccessRuleAudience { TenantId = _t, RuleId = r.Id, AudienceId = a });
        return r;
    }
    private async Task<AccessScope> Scope(FlowMindDbContext db, Guid audience)
    {
        await db.SaveChangesAsync();
        return AccessResolver.Build(await AccessModel.LoadAsync(db, _t, default), audience, "test");
    }

    [Fact]
    public async Task A_page_cited_by_two_items_is_open_only_to_audiences_that_may_see_both()
    {
        var db = Db(); var a = Page(db, "A", 2); var b = Page(db, "B", 2);   // both were taken from page 2
        Allow(db, "page", a.Id, _nurse, _ward); Allow(db, "page", b.Id, _ward);
        Assert.False((await Scope(db, _nurse)).Pages!.Allows(_doc, 2));      // Nurse may see A but not B, so the shared page is closed to her
        Assert.True((await Scope(db, _ward)).Pages!.Allows(_doc, 2));
    }

    [Fact]
    public async Task Pages_nothing_cites_follow_the_document_rule_and_are_closed_without_one()
    {
        var db = Db(); Page(db, "A", 2);
        Assert.False((await Scope(db, _nurse)).Pages!.Allows(_doc, 1));      // undecided = closed
        Allow(db, "document", _doc, _nurse);
        var nurse = await Scope(db, _nurse);
        Assert.True(nurse.Pages!.Allows(_doc, 1)); Assert.True(nurse.Pages.Allows(_doc, 3)); Assert.True(nurse.Pages.Allows(_doc, 4));
        Assert.False(nurse.Pages.Allows(_doc, 2));                           // page 2 is cited by A, which has no rule
        Assert.False((await Scope(db, _ward)).Pages!.Allows(_doc, 1));
    }

    [Fact]
    public async Task Everyone_opens_to_every_audience_and_an_empty_list_means_admins_only()
    {
        var db = Db(); var a = Page(db, "A", 2); var b = Page(db, "B", 3);
        var r = new AccessRule { TenantId = _t, TargetKind = "page", TargetId = a.Id, Everyone = true }; db.AccessRules.Add(r);
        Allow(db, "page", b.Id);                                              // decided, but nobody ticked
        foreach (var who in new[] { _nurse, _ward })
        {
            var s = await Scope(db, who);
            Assert.True(s.AllowsPage(a.Id)); Assert.False(s.AllowsPage(b.Id));
            Assert.True(s.Pages!.Allows(_doc, 2)); Assert.False(s.Pages.Allows(_doc, 3));
        }
    }

    [Fact]
    public async Task Rejected_items_claim_nothing_and_a_fully_open_document_is_reported_as_whole()
    {
        var db = Db(2);
        var rejected = Page(db, "Wrong extraction", 1, ReviewStatus.Rejected);   // must not hide page 1 forever
        Allow(db, "document", _doc, _nurse);
        var s = await Scope(db, _nurse);
        Assert.True(s.Pages!.Allows(_doc, 1)); Assert.True(s.Pages.Allows(_doc, 2));
        Assert.Contains(_doc, s.Pages.FullDocuments);                       // every page open: one entry instead of one per page
        Assert.Empty(s.Pages.Pages);
        Assert.False(s.AppPages.Contains(rejected.Id));
    }

    [Fact]
    public async Task A_workflow_is_visible_only_when_its_module_and_every_page_it_walks_through_are_open()
    {
        var db = Db();
        var mod = new Module { TenantId = _t, ApplicationId = _app, Name = "M", NormalizedName = "m", Review = ReviewStatus.Approved };
        db.Modules.Add(mod);
        var open = Page(db, "Open", 2, module: mod.Id); var hidden = Page(db, "Hidden", 3, module: mod.Id);
        var wf = new Workflow { TenantId = _t, ApplicationId = _app, ModuleId = mod.Id, Name = "Flow", NormalizedName = "flow", Review = ReviewStatus.Approved };
        db.Workflows.Add(wf);
        foreach (var (p, i) in new[] { (open, 1), (hidden, 2) })
            db.WorkflowSteps.Add(new WorkflowStep { TenantId = _t, ApplicationId = _app, WorkflowId = wf.Id, Order = i, Name = "s" + i, NormalizedName = "s", AppPageId = p.Id, Review = ReviewStatus.Approved });
        Allow(db, "module", mod.Id, _nurse); Allow(db, "page", open.Id, _nurse);
        var hiddenRule = Allow(db, "page", hidden.Id, _ward);
        Assert.False((await Scope(db, _nurse)).AllowsWorkflow(wf.Id));       // a visible workflow must never name a hidden page
        db.AccessRuleAudiences.Add(new AccessRuleAudience { TenantId = _t, RuleId = hiddenRule.Id, AudienceId = _nurse });
        Assert.True((await Scope(db, _nurse)).AllowsWorkflow(wf.Id));        // once the last page is open too, the workflow appears
    }

    [Fact]
    public async Task Keys_resolve_to_full_access_their_audience_or_nothing_and_never_fail_open()
    {
        var db = Db();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var plain = new TenantAccessKey { TenantId = _t, Name = "plain", KeyHash = "1" };
        var nurse = new TenantAccessKey { TenantId = _t, Name = "nurse", KeyHash = "2", AudienceId = _nurse };
        var revoked = new TenantAccessKey { TenantId = _t, Name = "revoked", KeyHash = "3", AudienceId = _nurse, IsActive = false };
        var dangling = new TenantAccessKey { TenantId = _t, Name = "dangling", KeyHash = "4", AudienceId = Guid.NewGuid() };
        db.AccessKeys.AddRange(plain, nurse, revoked, dangling);
        await db.SaveChangesAsync();
        var resolver = new AccessResolver(db, cache);

        Assert.True((await resolver.ForKeyAsync(_t, plain.Id, default)).Unrestricted);
        var n = await resolver.ForKeyAsync(_t, nurse.Id, default);
        Assert.False(n.Unrestricted); Assert.Equal("Nurse", n.Name);
        Assert.False((await resolver.ForKeyAsync(_t, revoked.Id, default)).Unrestricted);       // revoked: no access at all
        Assert.False((await resolver.ForKeyAsync(_t, Guid.NewGuid(), default)).Unrestricted);   // unknown key
        var d = await resolver.ForKeyAsync(_t, dangling.Id, default);
        Assert.False(d.Unrestricted); Assert.False(d.Pages!.Allows(_doc, 1));                    // an audience that no longer exists must not mean "everything"
        Assert.False((await resolver.ForKeyAsync(Guid.NewGuid(), nurse.Id, default)).Unrestricted);   // right key, wrong company
    }

    [Fact]
    public void Page_access_allows_whole_documents_and_listed_pages_only()
    {
        var d1 = Guid.NewGuid(); var d2 = Guid.NewGuid();
        var access = new PageAccess(new[] { d1 }, new[] { (d2, 2) });
        Assert.True(access.Allows(d1, 99)); Assert.True(access.Allows(d2, 2)); Assert.False(access.Allows(d2, 3)); Assert.False(access.Allows(Guid.NewGuid(), 2));
        Assert.False(new PageAccess(Array.Empty<Guid>(), Array.Empty<(Guid, int)>()).Allows(d1, 1));
    }


    [Theory]
    [InlineData("what can I ask?")]
    [InlineData("What can I search")]
    [InlineData("what data can be shown")]
    [InlineData("what approved for me to search")]
    [InlineData("what is available for me")]
    [InlineData("which topics do you have")]
    [InlineData("list the modules")]
    [InlineData("show me all pages available for me")]
    [InlineData("what do you know")]
    [InlineData("what can i see here")]
    public void Questions_about_coverage_are_recognised(string q) => Assert.True(FlowMind.Infrastructure.Chat.ScopeQuestion.IsMatch(q), q);

    [Theory]
    [InlineData("handover page explain")]
    [InlineData("what can I see on the dashboard page")]
    [InlineData("which pages show the validation rules")]
    [InlineData("how do I register a patient")]
    [InlineData("what are the roles allowed to open the control panel")]
    [InlineData("what is the next step after admission")]
    [InlineData("what does the save button do")]
    [InlineData("tell me about the bed board")]
    [InlineData("what")]
    public void Real_questions_about_the_documents_are_not_mistaken_for_coverage_questions(string q) => Assert.False(FlowMind.Infrastructure.Chat.ScopeQuestion.IsMatch(q), q);

    [Theory]
    [InlineData("what is the weather today")]
    [InlineData("tell me a joke")]
    [InlineData("write a poem about the sea")]
    [InlineData("who won the ipl match")]
    [InlineData("what is the capital of France")]
    [InlineData("latest news")]
    [InlineData("bitcoin price")]
    [InlineData("2 + 2")]
    [InlineData("what is 15 * 4")]
    [InlineData("are you chatgpt")]
    [InlineData("write python code to reverse a string")]
    [InlineData("இன்றைய வானிலை என்ன")]
    public void Plainly_unrelated_questions_are_recognised(string q) => Assert.True(FlowMind.Infrastructure.Chat.OffTopicQuestion.IsMatch(q), q);

    [Theory]
    [InlineData("how do I register a patient")]
    [InlineData("what is the temperature field in vitals")]
    [InlineData("what is the biggest file size allowed")]
    [InlineData("who is responsible for approving a handover")]
    [InlineData("what is the score on the triage page")]
    [InlineData("which validation applies to bed number 12")]
    [InlineData("what are the steps after admission")]
    [InlineData("what can I ask")]
    public void Real_questions_are_never_treated_as_unrelated(string q) => Assert.False(FlowMind.Infrastructure.Chat.OffTopicQuestion.IsMatch(q), q);

    [Fact]
    public void Unrelated_reply_says_what_can_be_asked_in_both_languages()
    {
        Assert.Contains("what can I ask?", FlowMind.Infrastructure.Chat.SmallTalk.Unrelated("Hospital System"));
        Assert.Contains("Hospital System", FlowMind.Infrastructure.Chat.SmallTalk.Unrelated("Hospital System"));
        Assert.Contains("what can I ask?", FlowMind.Infrastructure.Chat.SmallTalk.Unrelated("Hospital System", "ta"));
    }
    // ---- the suggestion rules ----

    private static readonly Guid Mgr = Guid.NewGuid(), Nur = Guid.NewGuid(), Wrd = Guid.NewGuid();
    private static readonly AudienceTerms[] Terms =
    {
        new(Mgr, "Manager", "supervisor, head"), new(Nur, "Nurse", "sister"), new(Wrd, "Ward", null),
    };

    [Theory]
    [InlineData("Manager", "Manager")]
    [InlineData("managers", "Manager")]
    [InlineData("Head", "Manager")]
    [InlineData("Staff Nurse", "Nurse")]
    [InlineData("Sister", "Nurse")]
    [InlineData("Ward users", "Ward")]
    [InlineData("Pharmacist", "")]
    public void Role_words_match_audience_names_and_aliases(string role, string expected)
    {
        var got = AccessSuggester.Match(role, Terms).Select(a => a.Name).ToList();
        Assert.Equal(expected.Length == 0 ? new List<string>() : new List<string> { expected }, got);
    }

    [Fact]
    public void An_exact_audience_name_wins_over_a_role_that_merely_contains_it()
    {
        var terms = new AudienceTerms[] { new(Nur, "Nurse", null), new(Wrd, "Ward Nurse", null) };
        Assert.Equal(new[] { "Ward Nurse" }, AccessSuggester.Match("Ward Nurse", terms).Select(a => a.Name));
        Assert.Equal(new[] { "Nurse" }, AccessSuggester.Match("Nurse", terms).Select(a => a.Name));
    }

    [Fact]
    public void Permissions_read_as_denials_are_not_suggested_and_everyone_is_recognised()
    {
        var page = Guid.NewGuid(); var other = Guid.NewGuid(); var none = Guid.NewGuid();
        var r = AccessSuggester.Suggest(new[]
        {
            (page, "Manager", "Can view and edit"), (page, "Nurse", "Cannot access this page"), (page, "Ward", "Not allowed"),
            (other, "All users", "Can view"),
            (none, "Pharmacist", "Can view"),
        }, Terms);
        Assert.Equal(new[] { Mgr }, r.Pages[page].AudienceIds);
        Assert.True(r.Pages[other].Everyone);
        Assert.False(r.Pages.ContainsKey(none));
        Assert.Equal(new[] { "Pharmacist" }, r.UnmatchedRoles);
    }

    [Fact]
    public void A_denial_beats_a_grant_for_the_same_role_on_the_same_page()
    {
        var page = Guid.NewGuid();
        var r = AccessSuggester.Suggest(new[] { (page, "Nurse", "Can view"), (page, "Nurse", "No access to the settings tab"), (page, "Manager", "Can view") }, Terms);
        Assert.Equal(new[] { Mgr }, r.Pages[page].AudienceIds);
    }
}
