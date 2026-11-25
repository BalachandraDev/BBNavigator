using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.AI;

/// <summary>
/// Enforces FlowMind's own daily caps (counted from AIUsageLog) before any provider call.
/// Limits default to 0 = blocked, so a forgotten setting can never cause unexpected usage.
/// For <see cref="AiProviderKind.DocumentAnalysis"/>, <c>requestedUnits</c> is the number of requests.
/// </summary>
public sealed class AiUsageService : IAiUsageService
{
    private readonly FlowMindDbContext _db;
    private readonly AiProvidersOptions _opt;

    private readonly UsageAttribution _who;

    public AiUsageService(FlowMindDbContext db, IOptions<AiProvidersOptions> opt, UsageAttribution? who = null)
    {
        _db = db;
        _opt = opt.Value;
        _who = who ?? new UsageAttribution();
    }

    public async Task EnsureWithinLimitAsync(AiProviderKind kind, string provider, int requestedUnits, CancellationToken ct)
    {
        switch (kind)
        {
            case AiProviderKind.Ocr:
                await EnsureOcrAsync(provider, requestedUnits, ct);
                break;
            case AiProviderKind.DocumentAnalysis:
                await EnsureGeminiAsync(provider, requestedUnits, ct);
                break;
            case AiProviderKind.Embedding:
                await EnsureNeuronsAsync(provider, requestedUnits, ct);
                break;
            case AiProviderKind.Chat:
                // Tamil answers come from Gemini and use the same free daily quota as document analysis; everything else is Groq.
                if (provider == GeminiAnalysisProvider.ProviderName) await EnsureGeminiChatAsync(provider, requestedUnits, ct);
                else await EnsureGroqAsync(provider, requestedUnits, ct);
                break;
            default:
                throw new NotSupportedException($"No usage limit rules for {kind} yet.");
        }
    }

    /// <summary>Groq free limits are per UTC day. requestedUnits = requests (normally 1).</summary>
    private async Task EnsureGroqAsync(string provider, int requests, CancellationToken ct)
    {
        var g = _opt.Groq;
        var today = DateTime.UtcNow.Date;
        if (g.DailyRequestLimit <= 0)
            throw new QuotaExceededException(provider,
                "Chat is disabled: set AiProviders:Groq:DailyRequestLimit from your Groq limits page.", today.AddDays(1));

        var counted = _db.AiUsageLogs.Where(l => l.Provider == provider && l.ProviderKind == AiProviderKind.Chat && l.TimestampUtc >= today
            && (l.Status == AiRequestStatus.Success || l.Status == AiRequestStatus.Failed || l.Status == AiRequestStatus.RateLimited));
        var used = await counted.CountAsync(ct);
        if (used + requests > g.DailyRequestLimit)
            throw new QuotaExceededException(provider,
                $"Daily chat request limit reached ({used}/{g.DailyRequestLimit}). Resets {today.AddDays(1):yyyy-MM-dd} 00:00 UTC.", today.AddDays(1));

        if (g.DailyTokenLimit > 0)
        {
            var tokens = await counted.SumAsync(l => l.InputTokens + l.OutputTokens, ct);
            if (tokens >= g.DailyTokenLimit)
                throw new QuotaExceededException(provider,
                    $"Daily chat token limit reached ({tokens}/{g.DailyTokenLimit}). Resets {today.AddDays(1):yyyy-MM-dd} 00:00 UTC.", today.AddDays(1));
        }
    }

    /// <summary>Cloudflare Workers AI free allocation is counted in neurons per UTC day. requestedUnits = estimated neurons.</summary>
    private async Task EnsureNeuronsAsync(string provider, int neurons, CancellationToken ct)
    {
        var limit = _opt.Cloudflare.DailyNeuronLimit;
        var today = DateTime.UtcNow.Date;
        if (limit <= 0)
            throw new QuotaExceededException(provider,
                "Embedding is disabled: set AiProviders:Cloudflare:DailyNeuronLimit (free allowance is 10,000 neurons/day; use less).", today.AddDays(1));

        var used = await _db.AiUsageLogs
            .Where(l => l.Provider == provider && l.ProviderKind == AiProviderKind.Embedding && l.TimestampUtc >= today && l.Status == AiRequestStatus.Success)
            .SumAsync(l => l.EmbeddingUnits, ct);

        if (used + neurons > limit)
            throw new QuotaExceededException(provider,
                $"Daily embedding limit reached ({used:0}/{limit} neurons used, ~{neurons} requested). Resets {today.AddDays(1):yyyy-MM-dd} 00:00 UTC.", today.AddDays(1));
    }

    private async Task EnsureOcrAsync(string provider, int pages, CancellationToken ct)
    {
        var limit = _opt.MistralOcr.DailyPageLimit;
        var today = DateTime.UtcNow.Date;
        if (limit <= 0)
            throw new QuotaExceededException(provider,
                "OCR is disabled: set AiProviders:MistralOcr:DailyPageLimit from your account's real free allowance.", today.AddDays(1));

        var used = await _db.AiUsageLogs
            .Where(l => l.Provider == provider && l.ProviderKind == AiProviderKind.Ocr && l.TimestampUtc >= today && l.Status == AiRequestStatus.Success)
            .SumAsync(l => l.OcrPages, ct);

        if (used + pages > limit)
            throw new QuotaExceededException(provider,
                $"Daily OCR page limit reached ({used}/{limit} used, {pages} requested). Retry after {today.AddDays(1):yyyy-MM-dd} 00:00 UTC.",
                today.AddDays(1));
    }

    private async Task EnsureGeminiAsync(string provider, int requests, CancellationToken ct)
    {
        var g = _opt.Gemini;
        var (dayStartUtc, nextResetUtc) = PacificDay(DateTime.UtcNow);
        if (g.DailyRequestLimit <= 0)
            throw new QuotaExceededException(provider,
                "AI analysis is disabled: set AiProviders:Gemini:DailyRequestLimit from your AI Studio rate-limit page.", nextResetUtc);

        // Failed/rate-limited calls also consume provider quota, so they count; cached ones do not.
        var counted = _db.AiUsageLogs.Where(l => l.Provider == provider && (l.ProviderKind == AiProviderKind.DocumentAnalysis || l.ProviderKind == AiProviderKind.Chat)
            && l.TimestampUtc >= dayStartUtc
            && (l.Status == AiRequestStatus.Success || l.Status == AiRequestStatus.Failed || l.Status == AiRequestStatus.RateLimited));

        var usedRequests = await counted.CountAsync(ct);
        if (usedRequests + requests > g.DailyRequestLimit)
            throw new QuotaExceededException(provider,
                $"Daily Gemini request limit reached ({usedRequests}/{g.DailyRequestLimit}). Resets {nextResetUtc:yyyy-MM-dd HH:mm} UTC (midnight Pacific).", nextResetUtc);

        if (g.DailyTokenLimit > 0)
        {
            var usedTokens = await counted.SumAsync(l => l.InputTokens + l.OutputTokens, ct);
            if (usedTokens >= g.DailyTokenLimit)
                throw new QuotaExceededException(provider,
                    $"Daily Gemini token limit reached ({usedTokens}/{g.DailyTokenLimit}). Resets {nextResetUtc:yyyy-MM-dd HH:mm} UTC.", nextResetUtc);
        }
    }

    /// <summary>Tamil chat on Gemini: its own daily caps first, then the shared Gemini total (analysis + chat). Same Pacific day as Google's quota.</summary>
    private async Task EnsureGeminiChatAsync(string provider, int requests, CancellationToken ct)
    {
        var g = _opt.Gemini;
        var (dayStartUtc, nextResetUtc) = PacificDay(DateTime.UtcNow);
        var chat = _db.AiUsageLogs.Where(l => l.Provider == provider && l.ProviderKind == AiProviderKind.Chat && l.TimestampUtc >= dayStartUtc
            && (l.Status == AiRequestStatus.Success || l.Status == AiRequestStatus.Failed || l.Status == AiRequestStatus.RateLimited));

        if (g.ChatDailyRequestLimit > 0)
        {
            var used = await chat.CountAsync(ct);
            if (used + requests > g.ChatDailyRequestLimit)
                throw new QuotaExceededException(provider,
                    $"Daily Tamil chat request limit reached ({used}/{g.ChatDailyRequestLimit}). Resets {nextResetUtc:yyyy-MM-dd HH:mm} UTC (midnight Pacific).", nextResetUtc);
        }
        if (g.ChatDailyTokenLimit > 0)
        {
            var tokens = await chat.SumAsync(l => l.InputTokens + l.OutputTokens, ct);
            if (tokens >= g.ChatDailyTokenLimit)
                throw new QuotaExceededException(provider,
                    $"Daily Tamil chat token limit reached ({tokens}/{g.ChatDailyTokenLimit}). Resets {nextResetUtc:yyyy-MM-dd HH:mm} UTC.", nextResetUtc);
        }
        await EnsureGeminiAsync(provider, requests, ct);   // and the shared total
    }
    /// <summary>Google's daily quotas reset at midnight Pacific; count usage on that boundary, not UTC midnight.</summary>
    public static (DateTime DayStartUtc, DateTime NextResetUtc) PacificDay(DateTime utcNow)
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
        catch (TimeZoneNotFoundException) { tz = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);
        var startLocal = local.Date;
        return (TimeZoneInfo.ConvertTimeToUtc(startLocal, tz), TimeZoneInfo.ConvertTimeToUtc(startLocal.AddDays(1), tz));
    }

    public async Task RecordAsync(AIUsageLog log, CancellationToken ct)
    {
        log.BotLinkId ??= _who.BotLinkId;   // a share-link request: every AI call it causes is counted for that link
        _db.AiUsageLogs.Add(log);
        await _db.SaveChangesAsync(ct);
    }
}
