using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlowMind.API.Authentication;
using FlowMind.Application.DTOs;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure;
using FlowMind.Infrastructure.AI;
using FlowMind.Infrastructure.Database;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowMind.Tests;

public class LimitsReportTests
{
    private sealed class Unscoped : ITenantContext { public Guid? TenantId => null; }
    private static readonly DateTime Now = new(2026, 11, 6, 7, 11, 0, DateTimeKind.Utc);   // 23:11 on Nov 5 in Pacific time

    private static (AiLimitsReport Report, FlowMindDbContext Db) Make(Action<AiProvidersOptions>? tweak = null)
    {
        var db = new FlowMindDbContext(new DbContextOptionsBuilder<FlowMindDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new Unscoped());
        var o = new AiProvidersOptions
        {
            Gemini = new GeminiOptions { DailyRequestLimit = 100, RequestsPerMinute = 8 },
            Groq = new GroqOptions { DailyRequestLimit = 50, DailyTokenLimit = 1000, RequestsPerMinute = 6 },
            Cloudflare = new CloudflareOptions { DailyNeuronLimit = 200 },
            MistralOcr = new MistralOcrOptions { DailyPageLimit = 0 },
        };
        tweak?.Invoke(o);
        return (new AiLimitsReport(db, Options.Create(o)), db);
    }

    private static AIUsageLog Log(string provider, AiProviderKind kind, AiRequestStatus status, DateTime at, int inTok = 0, int outTok = 0, decimal neurons = 0, int pages = 0, Guid? tenant = null) => new()
    {
        TenantId = tenant ?? Guid.NewGuid(), Provider = provider, ProviderKind = kind, Model = "m", Status = status, TimestampUtc = at,
        InputTokens = inTok, OutputTokens = outTok, EmbeddingUnits = neurons, OcrPages = pages, Operation = "x", Purpose = "x"
    };

    [Fact]
    public async Task Every_provider_is_reported_with_its_cap_pace_and_a_token_line_only_when_a_token_cap_is_set()
    {
        var (report, _) = Make();
        var lines = await report.GetAsync(Now, default);

        Assert.Equal(new[] { "gemini/requests", "gemini-chat/requests", "gemini-chat/tokens", "groq/requests", "groq/tokens", "cloudflare/neurons", "mistral/pages" }, lines.Select(l => $"{l.Provider}/{l.Metric}").ToArray());
        Assert.Equal(8, lines.Single(l => l.Provider == "gemini").PerMinute);
        Assert.Equal(150, lines.Single(l => l.Provider == "gemini-chat" && l.Metric == "requests").Limit);     // Tamil chat is its own gauge with its own cap
        Assert.Equal(300000, lines.Single(l => l.Provider == "gemini-chat" && l.Metric == "tokens").Limit);
        Assert.Equal(6, lines.First(l => l.Provider == "groq").PerMinute);
        Assert.Equal("off", lines.Single(l => l.Provider == "mistral").Status);          // limit 0 = feature off
        Assert.Equal(0, lines.Single(l => l.Provider == "mistral").Limit);
    }

    [Fact]
    public async Task Usage_is_counted_the_same_way_the_guard_counts_it_and_across_all_companies()
    {
        var (report, db) = Make();
        var t = Now.AddHours(-1);
        db.AiUsageLogs.AddRange(
            Log("groq", AiProviderKind.Chat, AiRequestStatus.Success, t, 400, 50),
            Log("groq", AiProviderKind.Chat, AiRequestStatus.Success, t, 300, 40),                       // another company: still counts
            Log("groq", AiProviderKind.Chat, AiRequestStatus.Failed, t, 10, 0),                           // reached the provider: counts
            Log("groq", AiProviderKind.Chat, AiRequestStatus.Cached, t),                                  // cached: free
            Log("groq", AiProviderKind.Chat, AiRequestStatus.QuotaExceeded, t),                           // blocked locally: free
            Log("groq", AiProviderKind.Chat, AiRequestStatus.Success, Now.Date.AddDays(-1).AddHours(23), 999, 0), // yesterday (UTC): not today
            Log("cloudflare", AiProviderKind.Embedding, AiRequestStatus.Success, t, neurons: 12.5m),
            Log("cloudflare", AiProviderKind.Embedding, AiRequestStatus.Failed, t, neurons: 50m));        // only successes count for neurons
        db.SaveChanges();

        var lines = await report.GetAsync(Now, default);
        var req = lines.Single(l => l.Provider == "groq" && l.Metric == "requests");
        var tok = lines.Single(l => l.Provider == "groq" && l.Metric == "tokens");
        Assert.Equal(3, req.Used);                       // 2 successes + 1 failure
        Assert.Equal(800, tok.Used);                     // 450 + 340 + 10
        Assert.Equal(80.0, tok.Percent);
        Assert.Equal("warn", tok.Status);                // 80% or more
        Assert.Equal("ok", req.Status);
        Assert.Equal(12.5, lines.Single(l => l.Provider == "cloudflare").Used);
    }

    [Fact]
    public async Task Status_moves_from_ok_to_warn_to_full_at_80_and_100_percent()
    {
        var (report, db) = Make(o => o.Gemini.DailyRequestLimit = 10);
        var pacificDayStart = AiUsageService.PacificDay(Now).DayStartUtc;
        string Status() => report.GetAsync(Now, default).Result.Single(l => l.Provider == "gemini").Status;

        for (int i = 0; i < 7; i++) db.AiUsageLogs.Add(Log("gemini", AiProviderKind.DocumentAnalysis, AiRequestStatus.Success, pacificDayStart.AddMinutes(5 + i)));
        db.SaveChanges(); Assert.Equal("ok", Status());                                  // 70%
        db.AiUsageLogs.Add(Log("gemini", AiProviderKind.DocumentAnalysis, AiRequestStatus.Success, pacificDayStart.AddMinutes(30)));
        db.SaveChanges(); Assert.Equal("warn", Status());                                // 80%
        db.AiUsageLogs.AddRange(Log("gemini", AiProviderKind.DocumentAnalysis, AiRequestStatus.Success, pacificDayStart.AddMinutes(31)),
                                Log("gemini", AiProviderKind.DocumentAnalysis, AiRequestStatus.RateLimited, pacificDayStart.AddMinutes(32)));
        db.SaveChanges(); Assert.Equal("full", Status());                                // 100%
    }

    [Fact]
    public async Task Resets_are_at_midnight_Pacific_for_Gemini_and_midnight_UTC_for_the_others()
    {
        var (report, _) = Make();
        var lines = await report.GetAsync(Now, default);

        Assert.Equal(new DateTime(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc), lines.Single(l => l.Provider == "gemini").ResetsAtUtc);   // 00:00 PST = 08:00 UTC
        Assert.Equal(new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc), lines.First(l => l.Provider == "groq").ResetsAtUtc);
        Assert.Equal(new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc), lines.Single(l => l.Provider == "cloudflare").ResetsAtUtc);
        Assert.All(lines, l => Assert.True(l.ResetsAtUtc > Now && l.ResetsAtUtc <= Now.AddHours(25)));
        Assert.Contains("Pacific", lines.Single(l => l.Provider == "gemini").ResetBasis);
        Assert.Equal("00:00 UTC", lines.Single(l => l.Provider == "cloudflare").ResetBasis);
    }

    [Fact]
    public async Task The_monthly_view_shows_month_to_date_use_and_the_capacity_the_daily_cap_allows()
    {
        var (report, db) = Make();
        db.AiUsageLogs.AddRange(
            Log("groq", AiProviderKind.Chat, AiRequestStatus.Success, new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc), 500, 100),   // earlier this month
            Log("groq", AiProviderKind.Chat, AiRequestStatus.Success, Now.AddHours(-2), 200, 50),                                          // today
            Log("groq", AiProviderKind.Chat, AiRequestStatus.Success, new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc), 9999, 0));   // last month: excluded
        db.SaveChanges();

        var tokens = (await report.GetAsync(Now, default)).Single(l => l.Provider == "groq" && l.Metric == "tokens");
        Assert.Equal(850, tokens.MonthUsed);                    // 600 + 250
        Assert.Equal(250, tokens.Used);                         // today only
        Assert.Equal(1000 * 30, tokens.MonthCapacity);          // November has 30 days
        Assert.Contains("monthly limit", tokens.MonthlyNote);
    }
}

public class UsageEndpointLimitsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public UsageEndpointLimitsTests(WebApplicationFactory<Program> factory) => _factory = factory.Configure().WithWebHostBuilder(b =>
    {
        b.UseSetting("AiProviders:Groq:DailyRequestLimit", "800");
        b.UseSetting("AiProviders:Groq:DailyTokenLimit", "160000");
        b.UseSetting("AiProviders:Cloudflare:DailyNeuronLimit", "8000");
    });

    [Fact]
    public async Task The_usage_endpoint_returns_the_caps_with_their_resets_next_to_the_companys_own_usage()
    {
        var tenant = Guid.NewGuid();
        ApiSecurityAndHealthTests.SeedTenant(_factory, tenant);
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Issue("tenant:t", Roles.TenantAdmin, tenant).Token);

        var json = await c.GetFromJsonAsync<JsonElement>("/api/v1/usage");
        var limits = json.GetProperty("limits").EnumerateArray().ToList();

        Assert.Contains(limits, l => l.GetProperty("provider").GetString() == "groq" && l.GetProperty("metric").GetString() == "tokens" && l.GetProperty("limit").GetInt64() == 160000);
        Assert.Contains(limits, l => l.GetProperty("provider").GetString() == "cloudflare" && l.GetProperty("limit").GetInt64() == 8000);
        Assert.All(limits, l => Assert.True(l.GetProperty("resetsAtUtc").GetDateTime() > DateTime.UtcNow.AddMinutes(-1)));
        Assert.True(json.TryGetProperty("today", out _) && json.TryGetProperty("thisMonth", out _));   // the company's own tables are still there
    }

    [Fact]
    public async Task Ordinary_users_still_cannot_see_usage()
    {
        var tenant = Guid.NewGuid(); ApiSecurityAndHealthTests.SeedTenant(_factory, tenant);
        var keyId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlowMindDbContext>();
            db.AccessKeys.Add(new TenantAccessKey { Id = keyId, TenantId = tenant, Name = "k", KeyHash = ApiKeys.Hash(keyId.ToString()), KeyPrefix = "fm_x" }); db.SaveChanges();
        }
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Issue($"key:{keyId:N}", Roles.TenantUser, tenant).Token);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/usage")).StatusCode);
    }
}
