using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlowMind.API.Authentication;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Infrastructure.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowMind.Tests;

/// <summary>The chat-only share link: public, tenant chosen by the server from the link secret, answers carry no source names.</summary>
public class BotLinkTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class FakeEmbedder : IEmbeddingProvider
    {
        public string Name => "cloudflare"; public int Dimensions => 4;
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> t, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<float[]>>(t.Select(_ => new float[] { 1, 0, 0, 0 }).ToList());
    }
    private sealed class FakeChat : IChatProvider
    {
        public string Name => "groq";
        public Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessageDto> m, int max, CancellationToken ct) =>
            Task.FromResult(new ChatCompletionResult("Full name is mandatory. [1]", 50, 10));
    }
    private sealed class FakeStore : IVectorStore
    {
        public Task<Dictionary<int, string>> GetIndexedHashesAsync(Guid t, Guid v, CancellationToken ct) => Task.FromResult(new Dictionary<int, string>());
        public Task UpsertAsync(Guid t, IReadOnlyList<ChunkRecord> c, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteVersionChunksNotInAsync(Guid t, Guid v, IReadOnlyCollection<int> k, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteOtherVersionsAsync(Guid t, Guid d, Guid k, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteDocumentAsync(Guid t, Guid d, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteTenantAsync(Guid t, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ChunkHit>> SearchAsync(Guid t, Guid a, string q, float[] e, int k, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChunkHit>>(new[] { new ChunkHit(Guid.NewGuid(), Guid.NewGuid(), "secret-guide.pdf", 3, "Full name is mandatory.", 0.03, 0.7) });
    }

    private readonly WebApplicationFactory<Program> _f;
    public BotLinkTests(WebApplicationFactory<Program> factory) => _f = factory.Configure().WithWebHostBuilder(b =>
    {
        b.UseSetting("AiProviders:Groq:DailyRequestLimit", "1000");
        b.UseSetting("AiProviders:Cloudflare:DailyNeuronLimit", "100000");
        b.ConfigureServices(s =>
        {
            foreach (var d in s.Where(x => x.ServiceType == typeof(IEmbeddingProvider) || x.ServiceType == typeof(IChatProvider) || x.ServiceType == typeof(IVectorStore)).ToList()) s.Remove(d);
            s.AddSingleton<IEmbeddingProvider, FakeEmbedder>(); s.AddSingleton<IChatProvider, FakeChat>(); s.AddSingleton<IVectorStore, FakeStore>();
        });
    });

    private Guid SeedTenant(out Guid app)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
        var t = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = t, Name = "Acme", Slug = "bot-" + t.ToString("N")[..8], ApiKeyHash = ApiKeys.Hash(ApiKeys.Generate()) });
        var a = new TenantApplication { TenantId = t, Name = "App" };
        db.Applications.Add(a); db.SaveChanges(); app = a.Id;
        return t;
    }

    private HttpClient Admin(Guid tenant)
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _f.Services.GetRequiredService<TokenService>().Issue("tenant:t", Roles.TenantAdmin, tenant).Token);
        return c;
    }

    private static async Task<(Guid Id, string Token)> Create(HttpClient admin, int limit = 300)
    {
        var res = await admin.PostAsJsonAsync("/api/v1/bot-links", new { name = "Site", title = "Acme Help", starters = new[] { "How do I register?" }, dailyMessageLimit = limit });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var j = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (j.GetProperty("id").GetGuid(), j.GetProperty("token").GetString()!);
    }

    private HttpClient Visitor(string? token, string visitor = "visitor-0001")
    {
        var c = _f.CreateClient();
        if (token != null) c.DefaultRequestHeaders.Add("X-Bot-Token", token);
        c.DefaultRequestHeaders.Add("X-Bot-Visitor", visitor);
        return c;
    }

    [Fact]
    public async Task A_visitor_needs_no_sign_in_and_gets_the_answer_text_only()
    {
        var t = SeedTenant(out _);
        var (_, token) = await Create(Admin(t));

        var info = await (await Visitor(token).GetAsync("/api/bot/info")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Acme Help", info.GetProperty("title").GetString());
        Assert.Equal("How do I register?", info.GetProperty("starters")[0].GetString());

        var res = await Visitor(token).PostAsJsonAsync("/api/bot/ask", new { message = "Is full name mandatory?" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        var j = JsonDocument.Parse(body).RootElement;
        Assert.Equal("Full name is mandatory.", j.GetProperty("answer").GetString());
        Assert.True(j.GetProperty("documented").GetBoolean());
        Assert.DoesNotContain("secret-guide", body);     // no document names
        Assert.False(j.TryGetProperty("sources", out _));
        Assert.False(j.TryGetProperty("related", out _));
    }

    [Fact]
    public async Task Unknown_wrong_disabled_deleted_links_and_disabled_companies_all_get_404()
    {
        var t = SeedTenant(out _);
        var admin = Admin(t);
        Assert.Equal(HttpStatusCode.NotFound, (await Visitor(null).GetAsync("/api/bot/info")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Visitor("fm_not-a-real-link-token-at-all").GetAsync("/api/bot/info")).StatusCode);

        var (id, token) = await Create(admin);
        Assert.Equal(HttpStatusCode.OK, (await Visitor(token).GetAsync("/api/bot/info")).StatusCode);

        await admin.PatchAsJsonAsync("/api/v1/bot-links/" + id, new { active = false });
        Assert.Equal(HttpStatusCode.NotFound, (await Visitor(token).PostAsJsonAsync("/api/bot/ask", new { message = "hello there" })).StatusCode);
        await admin.PatchAsJsonAsync("/api/v1/bot-links/" + id, new { active = true });
        Assert.Equal(HttpStatusCode.OK, (await Visitor(token).GetAsync("/api/bot/info")).StatusCode);

        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
            db.Tenants.Single(x => x.Id == t).IsActive = false; db.SaveChanges();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await Visitor(token).GetAsync("/api/bot/info")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_link_stops_it_at_once()
    {
        var t = SeedTenant(out _);
        var admin = Admin(t);
        var (id, token) = await Create(admin);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/v1/bot-links/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Visitor(token).GetAsync("/api/bot/info")).StatusCode);
    }

    [Fact]
    public async Task The_daily_limit_is_enforced()
    {
        var t = SeedTenant(out _);
        var (_, token) = await Create(Admin(t), limit: 2);
        Assert.Equal(HttpStatusCode.OK, (await Visitor(token).PostAsJsonAsync("/api/bot/ask", new { message = "question one" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Visitor(token, "visitor-0002").PostAsJsonAsync("/api/bot/ask", new { message = "question two" })).StatusCode);
        var third = await Visitor(token).PostAsJsonAsync("/api/bot/ask", new { message = "question three" });
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Contains("daily limit", await third.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_link_opens_no_other_api_and_conversations_belong_to_one_visitor()
    {
        var t = SeedTenant(out _);
        var (_, token) = await Create(Admin(t));
        var v = Visitor(token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await v.GetAsync("/api/v1/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await v.GetAsync("/api/v1/bot-links")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await v.GetAsync("/api/v1/usage")).StatusCode);

        var first = await (await v.PostAsJsonAsync("/api/bot/ask", new { message = "Is full name mandatory?" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = first.GetProperty("conversationId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await v.PostAsJsonAsync("/api/bot/ask", new { message = "and the date of birth?", conversationId = id })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Visitor(token, "someone-else-1").PostAsJsonAsync("/api/bot/ask", new { message = "and the date of birth?", conversationId = id })).StatusCode);
    }

    [Fact]
    public async Task One_company_cannot_see_or_change_anothers_links()
    {
        var a = SeedTenant(out _); var b = SeedTenant(out _);
        var (id, _) = await Create(Admin(a));
        Assert.Equal(HttpStatusCode.NotFound, (await Admin(b).DeleteAsync("/api/v1/bot-links/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin(b).PatchAsJsonAsync("/api/v1/bot-links/" + id, new { active = false })).StatusCode);
        var list = await Admin(b).GetFromJsonAsync<JsonElement>("/api/v1/bot-links");
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task The_bot_page_is_served_anonymously_with_the_strict_CSP_and_no_admin_code()
    {
        var c = _f.CreateClient();
        var res = await c.GetAsync("/bot.html");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("script-src 'self'", string.Join(";", res.Headers.GetValues("Content-Security-Policy")));
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("/js/bot.js", html);
        Assert.DoesNotContain("main.js", html);
        var js = await (await c.GetAsync("/js/bot.js")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("views/", js);              // none of the admin screens
        Assert.DoesNotContain("api/v1", js);
        Assert.DoesNotContain("sessionStorage", js);      // no admin session is read
    }
    [Fact]
    public async Task A_link_can_be_limited_to_chosen_documents_and_carries_an_accent_colour()
    {
        var t = SeedTenant(out var app);
        var admin = Admin(t);
        Guid doc;
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
            var d = new Document { TenantId = t, ApplicationId = app, FileName = "billing.pdf" };
            db.Documents.Add(d); db.SaveChanges(); doc = d.Id;
        }

        // a document that is not this application's is refused; so is a bad colour
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/bot-links", new { name = "x y", title = "Bot", documentIds = new[] { Guid.NewGuid() } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/bot-links", new { name = "x y", title = "Bot", accentColor = "red" })).StatusCode);

        var res = await admin.PostAsJsonAsync("/api/v1/bot-links", new { name = "Billing team", title = "Billing Helper", accentColor = "#0F766E", documentIds = new[] { doc } });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var j = await res.Content.ReadFromJsonAsync<JsonElement>();
        var token = j.GetProperty("token").GetString()!;
        Assert.Equal("billing.pdf", j.GetProperty("documentNames")[0].GetString());

        var info = await (await Visitor(token).GetAsync("/api/bot/info")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("#0f766e", info.GetProperty("accentColor").GetString());

        // the fake store's only hit belongs to a different document, so a scoped link cannot answer from it
        var scoped = await (await Visitor(token).PostAsJsonAsync("/api/bot/ask", new { message = "Is full name mandatory?" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("what can I ask?", scoped.GetProperty("answer").GetString());   // nothing in its documents comes close: the "outside what I can help with" message

        // removing the restriction lets it answer from everything
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync("/api/v1/bot-links/" + j.GetProperty("id").GetGuid(), new { documentIds = Array.Empty<Guid>() })).StatusCode);
        var open = await (await Visitor(token, "visitor-0009").PostAsJsonAsync("/api/bot/ask", new { message = "Is full name mandatory?" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(open.GetProperty("documented").GetBoolean());
    }

    private static async Task<Guid> AskOnce(HttpClient v, string q = "Is full name mandatory?")
    {
        var r = await v.PostAsJsonAsync("/api/bot/ask", new { message = q });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conversationId").GetGuid();
    }

    [Fact]
    public async Task A_visitor_sees_only_their_own_earlier_chats_and_can_delete_them()
    {
        var t = SeedTenant(out _);
        var (_, token) = await Create(Admin(t));
        var a = Visitor(token, "visitor-aaaa1"); var b = Visitor(token, "visitor-bbbb2");

        var c1 = await AskOnce(a);
        var list = await (await a.GetAsync("/api/bot/conversations")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(c1, list[0].GetProperty("id").GetGuid());

        var msgs = await (await a.GetAsync("/api/bot/conversations/" + c1)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "user", "assistant" }, msgs.EnumerateArray().Select(m => m.GetProperty("role").GetString()).ToArray());
        Assert.DoesNotContain("secret-guide", msgs.ToString());              // history carries no document names either

        // another visitor, and a client with no visitor id, see nothing and cannot read or delete it
        Assert.Equal(0, (await (await b.GetAsync("/api/bot/conversations")).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync("/api/bot/conversations/" + c1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync("/api/bot/conversations/" + c1)).StatusCode);
        Assert.Equal(0, (await (await Visitor(token, "x").GetAsync("/api/bot/conversations")).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());

        // the owner deletes one chat, then clears the rest
        await AskOnce(a); await AskOnce(a);
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync("/api/bot/conversations/" + c1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync("/api/bot/conversations/" + c1)).StatusCode);
        Assert.Equal(2, (await (await a.GetAsync("/api/bot/conversations")).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync("/api/bot/conversations")).StatusCode);
        Assert.Equal(0, (await (await a.GetAsync("/api/bot/conversations")).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());

        // The page uses POST (a proxy such as IIS/WebDAV may answer DELETE with 405). Same rules: only the owner's chats.
        var c4 = await AskOnce(a); var c5 = await AskOnce(a); var c6 = await AskOnce(b);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/bot/conversations/{c4}/delete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.PostAsync($"/api/bot/conversations/{c4}/delete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync("/api/bot/conversations/" + c4)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.PostAsync("/api/bot/conversations/clear", null)).StatusCode);
        Assert.Equal(0, (await (await a.GetAsync("/api/bot/conversations")).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
        Assert.Equal(1, (await (await b.GetAsync("/api/bot/conversations")).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());   // the other visitor keeps theirs
        Assert.Equal(HttpStatusCode.NotFound, (await Visitor(token, "x").PostAsync("/api/bot/conversations/clear", null)).StatusCode);   // no valid visitor id: nothing to clear
    }

    [Fact]
    public async Task The_admin_can_erase_one_links_chats_and_deleting_a_link_leaves_none_behind()
    {
        var t = SeedTenant(out _);
        var admin = Admin(t);
        var (id1, token1) = await Create(admin);
        var (id2, token2) = await Create(admin);
        var a = Visitor(token1, "visitor-aaaa1"); var b = Visitor(token1, "visitor-bbbb2"); var other = Visitor(token2, "visitor-cccc3");
        await AskOnce(a); await AskOnce(a); await AskOnce(b); await AskOnce(other);

        async Task<int> Stored(Guid id) => (await (await admin.GetAsync("/api/v1/bot-links")).Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().First(x => x.GetProperty("id").GetGuid() == id).GetProperty("storedChats").GetInt32();
        Assert.Equal(3, await Stored(id1));
        Assert.Equal(1, await Stored(id2));

        // clearing link 1 erases every visitor's chats on it, and only on it
        var r = await admin.DeleteAsync($"/api/v1/bot-links/{id1}/conversations");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(3, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("erased").GetInt32());
        Assert.Equal(0, await Stored(id1));
        Assert.Equal(1, await Stored(id2));
        Assert.Equal(0, (await (await a.GetAsync("/api/bot/conversations")).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/api/bot/ask", new { message = "Is full name mandatory?" })).StatusCode);   // the link still works

        // another company's admin cannot touch this link's chats
        var t2 = SeedTenant(out _);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin(t2).DeleteAsync($"/api/v1/bot-links/{id2}/conversations")).StatusCode);
        Assert.Equal(1, await Stored(id2));

        // deleting the link takes its chats with it
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/bot-links/{id2}")).StatusCode);
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
        Assert.Equal(0, db.Conversations.IgnoreQueryFilters().Count(c => c.OwnerId.StartsWith($"bot:{id2:N}:")));
        Assert.Equal(0, db.ConversationMessages.IgnoreQueryFilters().Count(m => !db.Conversations.IgnoreQueryFilters().Any(c => c.Id == m.ConversationId)));
    }

    private static Task<HttpResponseMessage> Patch(HttpClient c, string url, object body) =>
        c.SendAsync(new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body) });

    [Fact]
    public async Task A_visitor_can_send_a_question_to_support_and_only_that_companys_admin_sees_it()
    {
        var t = SeedTenant(out var app);
        var admin = Admin(t);
        var (_, token) = await Create(admin);
        var v = Visitor(token, "visitor-support1");
        var convo = await AskOnce(v);

        Assert.Equal(HttpStatusCode.NotFound, (await Visitor(null).PostAsJsonAsync("/api/bot/support", new { conversationId = convo })).StatusCode);               // no link
        Assert.Equal(HttpStatusCode.NotFound, (await Visitor(token, "visitor-other999").PostAsJsonAsync("/api/bot/support", new { conversationId = convo })).StatusCode);   // not their chat
        Assert.Equal(HttpStatusCode.BadRequest, (await v.PostAsJsonAsync("/api/bot/support", new { conversationId = convo, note = new string('x', 501) })).StatusCode);

        var sent = await v.PostAsJsonAsync("/api/bot/support", new { conversationId = convo, note = "Need this today", contact = "Ravi 98765 43210" });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.DoesNotContain("full name", await sent.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);       // the visitor learns nothing stored about others

        var inbox = await (await admin.GetAsync($"/api/v1/support-requests?applicationId={app}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, inbox.GetProperty("openTotal").GetInt32());
        var item = inbox.GetProperty("items")[0];
        var id = item.GetProperty("id").GetGuid();
        Assert.Equal("Is full name mandatory?", item.GetProperty("question").GetString());
        Assert.Equal("Site", item.GetProperty("channel").GetString());
        Assert.Equal("Need this today", item.GetProperty("note").GetString());
        Assert.Equal("Ravi 98765 43210", item.GetProperty("contact").GetString());
        Assert.True(item.GetProperty("wasAnswered").GetBoolean());

        // another company sees nothing and can change nothing
        var t2 = SeedTenant(out var app2);
        var other = Admin(t2);
        Assert.Equal(0, (await (await other.GetAsync($"/api/v1/support-requests?applicationId={app}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength());
        Assert.Equal(0, (await (await other.GetAsync("/api/v1/support-requests/count")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("open").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await Patch(other, $"/api/v1/support-requests/{id}", new { status = "done" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/v1/support-requests/{id}")).StatusCode);

        // the admin closes it with a note, then it moves from "open" to "done"
        var done = await Patch(admin, $"/api/v1/support-requests/{id}", new { status = "done", resolution = "Called back" });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.NotEqual(JsonValueKind.Null, (await done.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closedUtc").ValueKind);
        Assert.Equal(0, (await (await admin.GetAsync("/api/v1/support-requests/count")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("open").GetInt32());
        Assert.Equal(1, (await (await admin.GetAsync($"/api/v1/support-requests?applicationId={app}&status=done")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(admin, $"/api/v1/support-requests/{id}", new { status = "weird" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/support-requests/{id}")).StatusCode);
    }

    [Fact]
    public async Task The_admin_sees_unanswered_questions_and_a_period_report_for_their_own_application_only()
    {
        var t = SeedTenant(out var app);
        var admin = Admin(t);
        var (_, token) = await Create(admin);
        var v = Visitor(token, "visitor-gap0001");
        await AskOnce(v);                                                  // answered
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
            var c = new Conversation { TenantId = t, ApplicationId = app, OwnerId = "key:someone", Title = "q" };
            db.Conversations.Add(c);
            db.ConversationMessages.Add(new ConversationMessage { TenantId = t, ConversationId = c.Id, Role = "user", Content = "How do I cancel a bill?" });
            db.ConversationMessages.Add(new ConversationMessage { TenantId = t, ConversationId = c.Id, Role = "assistant", Content = "no", Grounded = false, CitationsJson = "[{\"Document\":\"billing.pdf\",\"Page\":2}]" });
            db.SaveChanges();
        }

        var gaps =await (await admin.GetAsync($"/api/v1/insights/gaps?applicationId={app}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, gaps.GetProperty("open").GetInt32());
        var item = gaps.GetProperty("items")[0];
        Assert.Equal("How do I cancel a bill?", item.GetProperty("question").GetString());
        var key = item.GetProperty("key").GetString();

        var usage = await (await admin.GetAsync($"/api/v1/insights/usage?applicationId={app}&days=7")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, usage.GetProperty("questions").GetInt32()); Assert.Equal(1, usage.GetProperty("answered").GetInt32()); Assert.Equal(7, usage.GetProperty("daily").GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/v1/insights/gaps/state", new { applicationId = app, key, status = "ignored" })).StatusCode);
        Assert.Equal(0, (await (await admin.GetAsync($"/api/v1/insights/gaps?applicationId={app}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("open").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/insights/gaps/state", new { applicationId = app, key, status = "bogus" })).StatusCode);

        // another company cannot read this application's questions, report or decisions
        var other = Admin(SeedTenant(out _));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/insights/gaps?applicationId={app}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/insights/usage?applicationId={app}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/api/v1/insights/gaps/state", new { applicationId = app, key, status = "handled" })).StatusCode);
    }

    [Fact]
    public async Task Each_share_link_has_its_own_token_count_and_other_companies_never_appear()
    {
        var t = SeedTenant(out _);
        var admin = Admin(t);
        async Task<(Guid Id, string Token)> Make(string name)
        {
            var res = await admin.PostAsJsonAsync("/api/v1/bot-links", new { name, title = name + " help" });
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
            var j = await res.Content.ReadFromJsonAsync<JsonElement>();
            return (j.GetProperty("id").GetGuid(), j.GetProperty("token").GetString()!);
        }
        var site = await Make("Website"); var ward = await Make("Ward kiosk"); var unused = await Make("Unused");

        // different questions, so none is served from the answer cache; the fake model reports 50 tokens in and 10 out per call
        var v1 = Visitor(site.Token, "visitor-s0001"); var v2 = Visitor(ward.Token, "visitor-w0001");
        foreach (var q in new[] { "Is the full name mandatory for registration?", "Which fields are required on admission?", "What does the bed board show?" })
            Assert.Equal(HttpStatusCode.OK, (await v1.PostAsJsonAsync("/api/bot/ask", new { message = q })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await v2.PostAsJsonAsync("/api/bot/ask", new { message = "How is a patient transferred between wards?" })).StatusCode);
        await v1.PostAsJsonAsync("/api/bot/ask", new { message = "hello" });          // small talk: no AI call, so no tokens

        // an admin's own chat is not any link's usage
        await admin.PostAsJsonAsync("/api/v1/chat", new { applicationId = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/applications")).EnumerateArray().First().GetProperty("id").GetGuid(), message = "What are the discharge rules for patients?" });

        var u = await admin.GetFromJsonAsync<JsonElement>("/api/v1/bot-links/usage?days=30");
        JsonElement Row(Guid id) => u.GetProperty("links").EnumerateArray().Single(r => r.GetProperty("linkId").GetGuid() == id);
        Assert.Equal(3, Row(site.Id).GetProperty("aiCalls").GetInt32());
        Assert.Equal(150, Row(site.Id).GetProperty("inputTokens").GetInt64()); Assert.Equal(30, Row(site.Id).GetProperty("outputTokens").GetInt64());
        Assert.Equal(180, Row(site.Id).GetProperty("totalTokens").GetInt64()); Assert.Equal(60, Row(site.Id).GetProperty("tokensPerCall").GetInt64());
        Assert.Equal(1, Row(ward.Id).GetProperty("aiCalls").GetInt32()); Assert.Equal(60, Row(ward.Id).GetProperty("totalTokens").GetInt64());
        Assert.Equal(0, Row(unused.Id).GetProperty("totalTokens").GetInt64());         // listed even with no use
        Assert.True(Row(site.Id).GetProperty("questions").GetInt32() >= 3);
        Assert.True(u.GetProperty("otherTokens").GetInt64() >= 0);
        Assert.Equal(240 + u.GetProperty("otherTokens").GetInt64(), u.GetProperty("totalTokens").GetInt64());

        // another company sees none of this
        var other = Admin(SeedTenant(out _));
        var theirs = await other.GetFromJsonAsync<JsonElement>("/api/v1/bot-links/usage");
        Assert.Equal(0, theirs.GetProperty("links").GetArrayLength()); Assert.Equal(0, theirs.GetProperty("totalTokens").GetInt64());

        // deleting a link keeps its history in the totals
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/v1/bot-links/" + ward.Id)).StatusCode);
        u = await admin.GetFromJsonAsync<JsonElement>("/api/v1/bot-links/usage");
        var gone = u.GetProperty("links").EnumerateArray().Single(r => r.GetProperty("name").GetString() == "(deleted link)");
        Assert.Equal(60, gone.GetProperty("totalTokens").GetInt64());
    }

    [Fact]
    public async Task History_can_be_switched_off_and_old_chats_expire()
    {
        var t = SeedTenant(out _);
        var admin = Admin(t);
        var (id, token) = await Create(admin);
        var v = Visitor(token, "visitor-ret01");
        var c = await AskOnce(v);

        using (var scope = _f.Services.CreateScope())   // pretend the chat is 40 days old (default retention is 30)
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
            var convo = db.Conversations.IgnoreQueryFilters().First(x => x.Id == c); convo.UpdatedUtc = DateTime.UtcNow.AddDays(-40); db.SaveChanges();
        }
        Assert.Equal(0, (await (await v.GetAsync("/api/bot/conversations")).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await v.GetAsync("/api/bot/conversations/" + c)).StatusCode);

        var c2 = await AskOnce(v);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync("/api/v1/bot-links/" + id, new { theme = new { historyEnabled = false } })).StatusCode);
        Assert.Equal(0, (await (await v.GetAsync("/api/bot/conversations")).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await v.GetAsync("/api/bot/conversations/" + c2)).StatusCode);
    }

    [Fact]
    public async Task The_admin_controls_the_look_but_only_with_allow_listed_values()
    {
        var t = SeedTenant(out _);
        var admin = Admin(t);
        var (id, token) = await Create(admin);

        var theme = new { mode = "dark", background = "gradient", bg1 = "#0B1020", bg2 = "#4c1d95", font = "serif", fontSize = "lg", radius = "soft", bubble = "outline",
                          width = "wide", header = "glass", avatar = "bolt", avatarText = "🩺", placeholder = "Ask billing…", footer = "Demo data only", showThemeToggle = false, historyDays = 7 };
        var res = await admin.PatchAsJsonAsync("/api/v1/bot-links/" + id, new { theme });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var info = await (await Visitor(token).GetAsync("/api/bot/info")).Content.ReadFromJsonAsync<JsonElement>();
        var th = info.GetProperty("theme");
        Assert.Equal("dark", th.GetProperty("mode").GetString());
        Assert.Equal("#0b1020", th.GetProperty("bg1").GetString());     // normalised to lower case
        Assert.Equal("glass", th.GetProperty("header").GetString());
        Assert.False(th.GetProperty("showThemeToggle").GetBoolean());
        Assert.Equal(7, th.GetProperty("historyDays").GetInt32());
        var listed = (await (await admin.GetAsync("/api/v1/bot-links")).Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("theme");
        Assert.Equal("Ask billing…", listed.GetProperty("placeholder").GetString());

        // anything outside the allow-lists is refused: no free CSS, no colour names, no out-of-range retention
        foreach (var bad in new object[]
        {
            new { background = "url(https://evil.example/x.png)" }, new { bg1 = "red" }, new { bg1 = "#12345" }, new { font = "Comic Sans; } body{display:none" },
            new { avatar = "<script>" }, new { historyDays = 0 }, new { historyDays = 5000 }, new { footer = new string('x', 201) },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync("/api/v1/bot-links/" + id, new { theme = bad })).StatusCode);
        // the stored look is unchanged by the refused edits
        Assert.Equal("glass", (await (await Visitor(token).GetAsync("/api/bot/info")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("theme").GetProperty("header").GetString());
    }

    [Fact]
    public async Task Only_the_chat_page_may_be_framed_and_only_by_this_site()
    {
        var c = _f.CreateClient();
        var bot = await c.GetAsync("/bot.html");
        Assert.Equal("SAMEORIGIN", bot.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'self'", string.Join(";", bot.Headers.GetValues("Content-Security-Policy")));
        var admin = await c.GetAsync("/");
        Assert.Equal("DENY", admin.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", string.Join(";", admin.Headers.GetValues("Content-Security-Policy")));
    }
    [Fact]
    public async Task The_admin_list_names_each_links_application_so_the_editor_can_load_its_documents()
    {
        var t = SeedTenant(out var app);
        var admin = Admin(t);
        await Create(admin);
        var first = (await (await admin.GetAsync("/api/v1/bot-links")).Content.ReadFromJsonAsync<JsonElement>())[0];
        Assert.Equal(app, first.GetProperty("applicationId").GetGuid());   // without it the Customize screen asked for "undefined" and showed no documents
    }
    [Fact]
    public async Task Tamil_texts_and_the_default_language_are_stored_and_served_but_only_within_the_allowed_values()
    {
        var t = SeedTenant(out _);
        var admin = Admin(t);
        var (id, token) = await Create(admin);

        var res = await admin.PatchAsJsonAsync("/api/v1/bot-links/" + id, new { theme = new {
            defaultLanguage = "ta", showLanguageSwitch = true, titleTa = "மருத்துவமனை உதவி", welcomeTa = "வணக்கம்! கேளுங்கள்.",
            startersTa = new[] { "நோயாளியைப் பதிவு செய்வது எப்படி?", "  ", "அறிக்கையை யார் இறுதி செய்யலாம்?" }, placeholderTa = "கேள்வியை உள்ளிடுங்கள்…", footerTa = "ஆவணங்களிலிருந்து மட்டும்" } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var th = (await (await Visitor(token).GetAsync("/api/bot/info")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("theme");
        Assert.Equal("ta", th.GetProperty("defaultLanguage").GetString());
        Assert.Equal("மருத்துவமனை உதவி", th.GetProperty("titleTa").GetString());
        Assert.Equal(2, th.GetProperty("startersTa").GetArrayLength());           // blank entries dropped
        Assert.Equal("அறிக்கையை யார் இறுதி செய்யலாம்?", th.GetProperty("startersTa")[1].GetString());

        foreach (var bad in new object[] { new { defaultLanguage = "fr" }, new { startersTa = new[] { "a", "b", "c", "d", "e" } }, new { titleTa = new string('த', 121) }, new { footerTa = new string('த', 201) } })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync("/api/v1/bot-links/" + id, new { theme = bad })).StatusCode);
    }
    [Fact]
    public async Task The_visitors_selected_language_is_accepted_and_followed_and_nothing_else_is()
    {
        var t = SeedTenant(out _);
        var (_, token) = await Create(Admin(t));

        var ta = await (await Visitor(token).PostAsJsonAsync("/api/bot/ask", new { message = "hi", language = "ta" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("வணக்கம்!", ta.GetProperty("answer").GetString());
        var en = await (await Visitor(token, "visitor-0002").PostAsJsonAsync("/api/bot/ask", new { message = "வணக்கம்", language = "en" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("Hello!", en.GetProperty("answer").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Visitor(token).PostAsJsonAsync("/api/bot/ask", new { message = "hi", language = "fr" })).StatusCode);
    }}
