using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlowMind.API.Authentication;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Database;
using FlowMind.Infrastructure.DocumentProcessing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowMind.Tests;

public class AuthorizationMatrixTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public AuthorizationMatrixTests(WebApplicationFactory<Program> factory) => _factory = factory.Configure();

    private void Db(Action<FlowMindDbContext> work)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
        work(db); db.SaveChanges();
    }

    private HttpClient WithToken(string subject, string role, Guid tenant)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.Services.GetRequiredService<TokenService>().Issue(subject, role, tenant).Token);
        return c;
    }

    private HttpClient Admin(Guid tenant) { ApiSecurityAndHealthTests.SeedTenant(_factory, tenant); return WithToken("tenant:acme", Roles.TenantAdmin, tenant); }

    private (HttpClient Client, string Subject) User(Guid tenant, bool active = true)
    {
        ApiSecurityAndHealthTests.SeedTenant(_factory, tenant);
        var id = Guid.NewGuid();
        Db(db => db.AccessKeys.Add(new TenantAccessKey { Id = id, TenantId = tenant, Name = "k", KeyPrefix = "fm_x", KeyHash = ApiKeys.Hash(id.ToString()), IsActive = active }));
        var sub = $"key:{id:N}";
        return (WithToken(sub, Roles.TenantUser, tenant), sub);
    }

    [Fact]
    public void Every_endpoint_states_its_access_rule_and_only_the_known_ones_are_anonymous()
    {
        var endpoints = _factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();
        Assert.NotEmpty(endpoints);

        var anonymous = new SortedSet<string>();
        foreach (var e in endpoints)
        {
            var isAnon = e.Metadata.GetMetadata<IAllowAnonymous>() != null;
            var hasPolicy = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any();
            Assert.True(isAnon || hasPolicy, $"Endpoint {e.RoutePattern.RawText} has no explicit authorization rule.");
            if (isAnon) anonymous.Add(e.RoutePattern.RawText!);
        }
        Assert.Equal(new[] { "/health/live", "/health/ready", "api/bot/ask", "api/bot/conversations", "api/bot/conversations/{id:guid}", "api/bot/conversations/{id:guid}/delete", "api/bot/conversations/clear", "api/bot/info", "api/bot/support", "api/v1/auth/forgot", "api/v1/auth/login", "api/v1/auth/options", "api/v1/auth/reset", "api/v1/auth/token", "api/v1/auth/token-info", "api/v1/system/info" }, anonymous.ToArray());
    }

    [Theory]
    [InlineData("GET", "/api/v1/documents")]
    [InlineData("DELETE", "/api/v1/documents/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/v1/documents/00000000-0000-0000-0000-000000000001/approve")]
    [InlineData("POST", "/api/v1/documents/00000000-0000-0000-0000-000000000001/retry")]
    [InlineData("POST", "/api/v1/documents/00000000-0000-0000-0000-000000000001/reindex")]
    [InlineData("GET", "/api/v1/usage")]
    [InlineData("GET", "/api/v1/review?applicationId=00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT", "/api/v1/review/page/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/v1/access-keys")]
    [InlineData("POST", "/api/v1/access-keys")]
    [InlineData("DELETE", "/api/v1/access-keys/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/v1/bot-links")]
    [InlineData("POST", "/api/v1/bot-links")]
    [InlineData("DELETE", "/api/v1/bot-links/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/v1/insights/gaps?applicationId=00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/v1/insights/gaps/state")]
    [InlineData("GET", "/api/v1/insights/usage?applicationId=00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/v1/support-requests?applicationId=00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/v1/support-requests/count")]
    [InlineData("PATCH", "/api/v1/support-requests/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/v1/support-requests/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/v1/settings/storage")]
    [InlineData("PUT", "/api/v1/settings/storage")]
    [InlineData("POST", "/api/v1/settings/storage/test")]
    [InlineData("POST", "/api/v1/settings/storage/migrate")]
    [InlineData("GET", "/api/v1/settings/overview")]
    [InlineData("GET", "/api/v1/settings/activity")]
    [InlineData("GET", "/api/v1/settings/security")]
    [InlineData("POST", "/api/v1/applications")]
    [InlineData("POST", "/api/v1/tenants")]
    [InlineData("GET", "/api/v1/tenants")]
    [InlineData("GET", "/api/v1/tenants/00000000-0000-0000-0000-000000000001/key")]
    [InlineData("POST", "/api/v1/tenants/00000000-0000-0000-0000-000000000001/reset-key")]
    [InlineData("POST", "/api/v1/tenants/00000000-0000-0000-0000-000000000001/disable")]
    [InlineData("POST", "/api/v1/tenants/00000000-0000-0000-0000-000000000001/enable")]
    [InlineData("DELETE", "/api/v1/tenants/00000000-0000-0000-0000-000000000001?confirm=x")]
    public async Task Ordinary_users_cannot_use_admin_endpoints(string method, string url)
    {
        var (client, _) = User(Guid.NewGuid());
        var res = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
        { Content = method is "POST" or "PUT" ? JsonContent.Create(new { name = "x y", status = "Approved" }) : null });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/applications")]
    [InlineData("/api/v1/modules?applicationId=00000000-0000-0000-0000-000000000001")]
    [InlineData("/api/v1/workflows?applicationId=00000000-0000-0000-0000-000000000001")]
    [InlineData("/api/v1/conversations?applicationId=00000000-0000-0000-0000-000000000001")]
    public async Task Ordinary_users_can_read_knowledge_and_conversations(string url)
    {
        var (client, _) = User(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task A_revoked_key_or_a_deactivated_company_is_rejected_even_with_a_valid_token()
    {
        var (revoked, _) = User(Guid.NewGuid(), active: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await revoked.GetAsync("/api/v1/applications")).StatusCode);

        var tenant = Guid.NewGuid();
        ApiSecurityAndHealthTests.SeedTenant(_factory, tenant, active: false);
        var admin = WithToken("tenant:gone", Roles.TenantAdmin, tenant);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/v1/applications")).StatusCode);

        // A token for a company that does not exist at all is rejected too.
        var ghost = WithToken("tenant:ghost", Roles.TenantAdmin, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, (await ghost.GetAsync("/api/v1/applications")).StatusCode);
    }

    [Fact]
    public async Task Users_see_only_their_own_conversations()
    {
        var tenant = Guid.NewGuid(); var app = Guid.NewGuid();
        var (alice, aliceSub) = User(tenant); var (bob, bobSub) = User(tenant);
        Guid aliceConvo = Guid.NewGuid(), bobConvo = Guid.NewGuid();
        Db(db =>
        {
            db.Conversations.Add(new Conversation { Id = aliceConvo, TenantId = tenant, ApplicationId = app, OwnerId = aliceSub, Title = "alice q" });
            db.Conversations.Add(new Conversation { Id = bobConvo, TenantId = tenant, ApplicationId = app, OwnerId = bobSub, Title = "bob q" });
        });

        var list = await alice.GetFromJsonAsync<JsonElement>($"/api/v1/conversations?applicationId={app}");
        Assert.Equal(new[] { "alice q" }, list.EnumerateArray().Select(e => e.GetProperty("title").GetString()).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/conversations/{bobConvo}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync($"/api/v1/conversations/{bobConvo}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/v1/conversations/{aliceConvo}")).StatusCode);
    }

    [Fact]
    public async Task Users_see_only_approved_knowledge_while_admins_also_see_drafts()
    {
        var tenant = Guid.NewGuid(); var app = Guid.NewGuid();
        Guid draftPage = Guid.NewGuid(), approvedPage = Guid.NewGuid();
        Db(db =>
        {
            db.AppPages.Add(new AppPage { Id = approvedPage, TenantId = tenant, ApplicationId = app, Name = "Approved page", NormalizedName = "approved page", Review = ReviewStatus.Approved });
            db.AppPages.Add(new AppPage { Id = draftPage, TenantId = tenant, ApplicationId = app, Name = "Draft page", NormalizedName = "draft page", Review = ReviewStatus.Pending });
        });
        var (user, _) = User(tenant); var admin = Admin(tenant);

        async Task<string[]> PageNames(HttpClient c)
        {
            var r = await c.GetFromJsonAsync<JsonElement>($"/api/v1/modules?applicationId={app}");
            return r.GetProperty("modules").EnumerateArray().SelectMany(m => m.GetProperty("pages").EnumerateArray())
                .Select(p => p.GetProperty("name").GetString()!).OrderBy(x => x).ToArray();
        }
        Assert.Equal(new[] { "Approved page" }, await PageNames(user));
        Assert.Equal(new[] { "Approved page", "Draft page" }, await PageNames(admin));
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync($"/api/v1/pages/{approvedPage}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/api/v1/pages/{draftPage}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/pages/{draftPage}")).StatusCode);
    }

    [Fact]
    public async Task Another_companys_data_is_invisible_even_with_a_valid_user_token()
    {
        var tenantA = Guid.NewGuid(); var tenantB = Guid.NewGuid(); var appA = Guid.NewGuid();
        Db(db => db.AppPages.Add(new AppPage { TenantId = tenantA, ApplicationId = appA, Name = "A only", NormalizedName = "a only", Review = ReviewStatus.Approved }));
        var (userB, _) = User(tenantB);
        var r = await userB.GetFromJsonAsync<JsonElement>($"/api/v1/modules?applicationId={appA}");
        Assert.Empty(r.GetProperty("modules").EnumerateArray());
    }

    [Fact]
    public async Task Chat_and_search_are_rate_limited_per_user_to_protect_the_free_ai_quota()
    {
        var admin = Admin(Guid.NewGuid());
        var body = new { applicationId = Guid.NewGuid(), message = "hello there" };
        HttpStatusCode last = default; int ok = 0;
        for (int i = 0; i < 20; i++)
        {
            last = (await admin.PostAsJsonAsync("/api/v1/chat", body)).StatusCode;
            if (last == HttpStatusCode.TooManyRequests) break;
            ok++;
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        Assert.Equal(15, ok);
    }

    [Fact]
    public async Task User_keys_sign_in_as_ordinary_users_and_wrong_or_revoked_keys_do_not()
    {
        var tenant = Guid.NewGuid(); var good = ApiKeys.Generate(); var revoked = ApiKeys.Generate();
        Db(db =>
        {
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Acme", Slug = "acme-sec", ApiKeyPrefix = "fm_adm", ApiKeyHash = ApiKeys.Hash(ApiKeys.Generate()) });
            db.AccessKeys.Add(new TenantAccessKey { TenantId = tenant, Name = "good", KeyPrefix = good[..8], KeyHash = ApiKeys.Hash(good) });
            db.AccessKeys.Add(new TenantAccessKey { TenantId = tenant, Name = "old", KeyPrefix = revoked[..8], KeyHash = ApiKeys.Hash(revoked), IsActive = false });
        });
        var c = _factory.CreateClient();

        var ok = await c.PostAsJsonAsync("/api/v1/auth/token", new { tenantSlug = "acme-sec", apiKey = good });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var token = (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        var payload = JsonDocument.Parse(Convert.FromBase64String(token.Split('.')[1].Replace('-', '+').Replace('_', '/').PadRight((token.Split('.')[1].Length + 3) / 4 * 4, '=')));
        Assert.Equal(Roles.TenantUser, payload.RootElement.GetProperty("role").GetString());
        Assert.StartsWith("key:", payload.RootElement.GetProperty("sub").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/auth/token", new { tenantSlug = "acme-sec", apiKey = revoked })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/auth/token", new { tenantSlug = "acme-sec", apiKey = "fm_nonsense" })).StatusCode);
    }

    [Fact]
    public async Task Api_responses_are_not_cacheable_and_anonymous_info_does_not_leak_version_or_environment()
    {
        var res = await _factory.CreateClient().GetAsync("/api/v1/system/info");
        Assert.Contains("no-store", res.Headers.CacheControl?.ToString());
        var text = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("environment", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("version", text, StringComparison.OrdinalIgnoreCase);
    }
}

public class ProductionGuardTests
{
    private static IConfiguration Config(Dictionary<string, string?> v) => new ConfigurationBuilder().AddInMemoryCollection(v).Build();

    private static Dictionary<string, string?> Good() => new()
    {
        ["Auth:SigningKey"] = "k7Qe0b1y9sT3uVwXyZ2aB4cD6eF8gH0jL5mN7pR9tUw=",
        ["Auth:PlatformAdminKey"] = "Zr8LmQ2vX7nB4kT9wC6yH1dF3sG5jP0a",
        ["ConnectionStrings:SqlServer"] = "Server=sql01;Database=FlowMind;Integrated Security=true;Encrypt=true",
        ["ConnectionStrings:Postgres"] = "Host=pg01;Database=flowmind_vectors;Username=flowmind;Password=Str0ng-Random-Pw!",
        ["Storage:RootPath"] = @"D:\FlowMindData",
        ["Security:SecretsKey"] = "q8Zr3LmT7vX2nB4kW9cY6hD1fG5sJ0aP-secrets",
    };

    [Fact] public void A_properly_configured_production_setup_passes() => Assert.Empty(ProductionGuards.Check(Config(Good())));

    [Fact]
    public void Development_secrets_and_databases_are_refused_outside_development()
    {
        var dev = new Dictionary<string, string?>
        {
            ["Auth:SigningKey"] = "DEV-ONLY-signing-key-change-me-0123456789abcdef",
            ["Auth:PlatformAdminKey"] = "dev-platform-admin-key-change-me",
            ["ConnectionStrings:SqlServer"] = @"Server=(localdb)\MSSQLLocalDB;Database=FlowMind",
            ["ConnectionStrings:Postgres"] = "Host=localhost;Password=flowmind_dev_only",
            ["Storage:RootPath"] = "",
            ["Security:SecretsKey"] = "DEV-ONLY-secrets-key-0123456789abcdefghij",
        };
        Assert.Equal(6, ProductionGuards.Check(Config(dev)).Count);
    }

    [Fact]
    public void Platform_admin_login_may_be_disabled_by_leaving_the_key_empty()
    {
        var c = Good(); c["Auth:PlatformAdminKey"] = "";
        Assert.Empty(ProductionGuards.Check(Config(c)));
    }

    [Fact]
    public void Enforce_throws_in_production_but_not_in_development()
    {
        var bad = Config(new Dictionary<string, string?> { ["Auth:SigningKey"] = "short" });
        Assert.Throws<InvalidOperationException>(() => ProductionGuards.Enforce(bad, new TestEnv("Production")));
        ProductionGuards.Enforce(bad, new TestEnv("Development"));
    }

    private sealed class TestEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public TestEnv(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

public class TokenStatusCheckerTests
{
    private sealed class Unscoped : FlowMind.Application.Interfaces.ITenantContext { public Guid? TenantId => null; }
    private static FlowMindDbContext NewDb() => new(new DbContextOptionsBuilder<FlowMindDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new Unscoped());

    private static System.Security.Claims.ClaimsPrincipal Principal(string role, Guid tenant, string sub) => new(new System.Security.Claims.ClaimsIdentity(new[]
    {
        new System.Security.Claims.Claim("role", role), new System.Security.Claims.Claim("tid", tenant.ToString()), new System.Security.Claims.Claim("sub", sub)
    }, "test"));

    [Fact]
    public async Task Revocation_takes_effect_once_the_short_cache_expires_and_never_applies_to_platform_admins()
    {
        using var db = NewDb();
        var tenant = Guid.NewGuid(); var keyId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "A", Slug = "a" });
        var key = new TenantAccessKey { Id = keyId, TenantId = tenant, Name = "k", KeyHash = "h" };
        db.AccessKeys.Add(key); db.SaveChanges();
        var user = Principal(Roles.TenantUser, tenant, $"key:{keyId:N}");

        var cache = new MemoryCache(new MemoryCacheOptions());
        Assert.True(await new TokenStatusChecker(cache).IsStillValidAsync(user, db, default));

        key.IsActive = false; db.SaveChanges();
        Assert.True(await new TokenStatusChecker(cache).IsStillValidAsync(user, db, default));                       // cached for a few seconds
        Assert.False(await new TokenStatusChecker(new MemoryCache(new MemoryCacheOptions())).IsStillValidAsync(user, db, default)); // fresh lookup sees the revocation

        Assert.True(await new TokenStatusChecker(new MemoryCache(new MemoryCacheOptions())).IsStillValidAsync(Principal(Roles.PlatformAdmin, Guid.Empty, "platform-admin"), db, default));
    }

    [Fact]
    public async Task A_user_token_whose_subject_is_not_a_key_id_is_rejected()
    {
        using var db = NewDb();
        var tenant = Guid.NewGuid(); db.Tenants.Add(new Tenant { Id = tenant, Name = "A", Slug = "a" }); db.SaveChanges();
        Assert.False(await new TokenStatusChecker(new MemoryCache(new MemoryCacheOptions()))
            .IsStillValidAsync(Principal(Roles.TenantUser, tenant, "tenant:acme"), db, default));
    }
}

public class ZipGuardTests
{
    private static MemoryStream Zip(params (string Name, int Bytes)[] entries)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using var s = z.CreateEntry(name, CompressionLevel.Fastest).Open();
                var chunk = new byte[1024 * 1024];
                for (int written = 0; written < bytes; written += chunk.Length) s.Write(chunk, 0, Math.Min(chunk.Length, bytes - written));
            }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void A_small_package_passes_and_the_stream_is_left_at_the_start()
    {
        using var ms = Zip(("word/document.xml", 10_000));
        ZipGuard.Check(ms);
        Assert.Equal(0, ms.Position);
    }

    [Fact]
    public void A_package_that_unpacks_to_far_more_than_its_size_is_refused()
    {
        using var ms = Zip(("word/document.xml", 30 * 1024 * 1024));
        Assert.True(ms.Length < 1024 * 1024);                        // tiny on disk…
        Assert.Throws<FlowMind.Application.Interfaces.UnreadableDocumentException>(() => ZipGuard.Check(ms, maxUnpackedBytes: 10 * 1024 * 1024)); // …huge when unpacked
    }

    [Fact]
    public void Not_a_zip_is_reported_as_an_unreadable_document()
    {
        using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Throws<FlowMind.Application.Interfaces.UnreadableDocumentException>(() => ZipGuard.Check(ms));
    }
}
