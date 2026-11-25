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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowMind.Tests;

/// <summary>The simple integration path: POST /api/ask with an X-Api-Key header and a JSON message.</summary>
public class AskEndpointTests : IClassFixture<WebApplicationFactory<Program>>
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
            Task.FromResult<IReadOnlyList<ChunkHit>>(new[] { new ChunkHit(Guid.NewGuid(), Guid.NewGuid(), "guide.pdf", 3, "Full name is mandatory.", 0.03, 0.7) });
    }

    private readonly WebApplicationFactory<Program> _plain;   // AI providers not configured (the default, safe state)
    private readonly WebApplicationFactory<Program> _fake;    // AI providers replaced with fakes and limits raised
    public AskEndpointTests(WebApplicationFactory<Program> factory)
    {
        _plain = factory.Configure();
        _fake = factory.Configure().WithWebHostBuilder(b =>
        {
            b.UseSetting("AiProviders:Groq:DailyRequestLimit", "1000");
            b.UseSetting("AiProviders:Cloudflare:DailyNeuronLimit", "100000");
            b.ConfigureServices(s =>
            {
                foreach (var d in s.Where(x => x.ServiceType == typeof(IEmbeddingProvider) || x.ServiceType == typeof(IChatProvider) || x.ServiceType == typeof(IVectorStore)).ToList()) s.Remove(d);
                s.AddSingleton<IEmbeddingProvider, FakeEmbedder>(); s.AddSingleton<IChatProvider, FakeChat>(); s.AddSingleton<IVectorStore, FakeStore>();
            });
        });
    }

    private static string Seed(WebApplicationFactory<Program> f, int apps, bool keyActive = true, bool tenantActive = true, string slug = "acme", string[]? appNames = null)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
        var tenant = Guid.NewGuid(); var key = ApiKeys.Generate();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "Acme", Slug = slug + "-" + tenant.ToString("N")[..6], IsActive = tenantActive, ApiKeyHash = ApiKeys.Hash(ApiKeys.Generate()) });
        db.AccessKeys.Add(new TenantAccessKey { TenantId = tenant, Name = "k", KeyPrefix = key[..8], KeyHash = ApiKeys.Hash(key), IsActive = keyActive });
        for (int i = 0; i < apps; i++)
            db.TenantApplications_Add(tenant, appNames?[i] ?? "App " + (i + 1));
        db.SaveChanges();
        return key;
    }

    private static HttpClient Client(WebApplicationFactory<Program> f, string? key)
    {
        var c = f.CreateClient();
        if (key != null) c.DefaultRequestHeaders.Add("X-Api-Key", key);
        return c;
    }
    private static Task<HttpResponseMessage> Ask(HttpClient c, object body) => c.PostAsJsonAsync("/api/ask", body);

    [Fact]
    public async Task Missing_or_wrong_keys_are_refused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Ask(Client(_plain, null), new { message = "hi there" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Ask(Client(_plain, "fm_not-a-real-key-at-all"), new { message = "hi there" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Ask(Client(_plain, Seed(_plain, 1, keyActive: false)), new { message = "hi there" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Ask(Client(_plain, Seed(_plain, 1, tenantActive: false)), new { message = "hi there" })).StatusCode);
    }

    [Fact]
    public async Task One_call_returns_the_answer_with_its_document_and_page()
    {
        var c = Client(_fake, Seed(_fake, 1, appNames: new[] { "Hospital System" }));
        var res = await Ask(c, new { message = "Is full name mandatory?" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Full name is mandatory.", json.GetProperty("answer").GetString());   // citation numbers are not shown in the text; the source is in "sources"
        Assert.True(json.GetProperty("documented").GetBoolean());
        Assert.Equal("guide.pdf", json.GetProperty("sources")[0].GetProperty("document").GetString());
        Assert.Equal(3, json.GetProperty("sources")[0].GetProperty("page").GetInt32());
        Assert.Equal("Hospital System", json.GetProperty("application").GetString());
        Assert.NotEqual(Guid.Empty, json.GetProperty("conversationId").GetGuid());
    }

    [Fact]
    public async Task A_follow_up_uses_the_conversation_id_and_nobody_else_can_use_it()
    {
        var key = Seed(_fake, 1);
        var first = await (await Ask(Client(_fake, key), new { message = "Is full name mandatory?" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = first.GetProperty("conversationId").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await Ask(Client(_fake, key), new { message = "and the date of birth?", conversationId = id })).StatusCode);
        var otherKey = Seed(_fake, 1);
        Assert.Equal(HttpStatusCode.NotFound, (await Ask(Client(_fake, otherKey), new { message = "and the date of birth?", conversationId = id })).StatusCode);
    }

    [Fact]
    public async Task The_application_is_picked_automatically_when_there_is_only_one_and_must_be_named_when_there_are_several()
    {
        var one = Client(_fake, Seed(_fake, 1));
        Assert.Equal(HttpStatusCode.OK, (await Ask(one, new { message = "hello there" })).StatusCode);

        var many = Client(_fake, Seed(_fake, 2, appNames: new[] { "Billing", "Hospital" }));
        var ambiguous = await Ask(many, new { message = "hello there" });
        Assert.Equal(HttpStatusCode.BadRequest, ambiguous.StatusCode);
        var text = await ambiguous.Content.ReadAsStringAsync();
        Assert.Contains("Billing", text); Assert.Contains("Hospital", text);

        Assert.Equal(HttpStatusCode.OK, (await Ask(many, new { message = "hello there", application = "hospital" })).StatusCode);   // case-insensitive
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(many, new { message = "hello there", application = "Nope" })).StatusCode);
    }

    [Fact]
    public async Task A_company_without_an_application_gets_a_clear_message()
    {
        var res = await Ask(Client(_fake, Seed(_fake, 0)), new { message = "hello there" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("No application", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task With_no_ai_limits_configured_the_call_is_refused_with_429_and_a_reason_not_a_paid_fallback()
    {
        var res = await Ask(Client(_plain, Seed(_plain, 1)), new { message = "What does the registration page validate?" });
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Contains("disabled", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Bad_input_is_a_400()
    {
        var c = Client(_fake, Seed(_fake, 1));
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(c, new { message = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(c, new { message = new string('x', 8001) })).StatusCode);
    }

    [Fact]
    public async Task The_api_key_header_only_opens_the_simple_endpoint_not_the_admin_api()
    {
        var c = Client(_fake, Seed(_fake, 1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/usage")).StatusCode);
    }

    [Fact]
    public async Task A_normal_signed_in_token_works_on_the_same_endpoint()
    {
        var key = Seed(_fake, 1);
        var c = _fake.CreateClient();
        using var scope = _fake.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
        var tenant = db.AccessKeys.Single(k => k.KeyHash == ApiKeys.Hash(key));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _fake.Services.GetRequiredService<TokenService>().Issue($"key:{tenant.Id:N}", Roles.TenantUser, tenant.TenantId).Token);
        Assert.Equal(HttpStatusCode.OK, (await Ask(c, new { message = "hello there" })).StatusCode);
    }

    [Fact]
    public async Task Each_key_has_its_own_per_minute_allowance()
    {
        var noApp = Client(_fake, Seed(_fake, 0));            // answers 409 instantly, but each call still counts
        HttpStatusCode last = default; int allowed = 0;
        for (int i = 0; i < 25; i++)
        {
            last = (await Ask(noApp, new { message = "hello there" })).StatusCode;
            if (last == HttpStatusCode.TooManyRequests) break;
            allowed++;
        }
        Assert.Equal(15, allowed);
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        Assert.Equal(HttpStatusCode.Conflict, (await Ask(Client(_fake, Seed(_fake, 0)), new { message = "hello there" })).StatusCode); // a different key is unaffected
    }
}

internal static class SeedExtensions
{
    public static void TenantApplications_Add(this FlowMindDbContext db, Guid tenant, string name) =>
        db.Applications.Add(new TenantApplication { TenantId = tenant, Name = name });
}
