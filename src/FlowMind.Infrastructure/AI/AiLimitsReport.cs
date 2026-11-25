using FlowMind.Application.DTOs;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.AI;

/// <summary>
/// Reports every daily cap FlowMind enforces (the same numbers and the same day boundaries <see cref="AiUsageService"/> uses),
/// how much of it is used, and when it resets — for the Usage dashboard. Read-only.
/// </summary>
public sealed class AiLimitsReport
{
    private readonly FlowMindDbContext _db;
    private readonly AiProvidersOptions _opt;
    public AiLimitsReport(FlowMindDbContext db, IOptions<AiProvidersOptions> opt) { _db = db; _opt = opt.Value; }

    private sealed record Sums(int Requests, long Tokens, double Neurons, long Pages);

    public Task<IReadOnlyList<LimitLine>> GetAsync(CancellationToken ct) => GetAsync(DateTime.UtcNow, ct);

    public async Task<IReadOnlyList<LimitLine>> GetAsync(DateTime nowUtc, CancellationToken ct)
    {
        var utcDay = nowUtc.Date;
        var nextUtc = utcDay.AddDays(1);
        var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var days = DateTime.DaysInMonth(nowUtc.Year, nowUtc.Month);
        var (pacStart, pacNext) = AiUsageService.PacificDay(nowUtc);

        // Calls that reached the provider count against its quota even when they failed; cached and blocked ones do not.
        var spent = new[] { AiRequestStatus.Success, AiRequestStatus.Failed, AiRequestStatus.RateLimited };
        var success = new[] { AiRequestStatus.Success };

        var lines = new List<LimitLine>();
        LimitLine Line(string provider, string metric, string unit, long limit, double used, DateTime start, DateTime reset, string basis, int? rpm, double month, string note)
        {
            var pct = limit > 0 ? Math.Round(used / limit * 100, 1) : 0;
            var status = limit <= 0 ? "off" : pct >= 100 ? "full" : pct >= 80 ? "warn" : "ok";
            return new LimitLine(provider, metric, unit, Math.Max(0, limit), used, pct, status, start, reset, basis, rpm, month, Math.Max(0, limit) * days, note);
        }

        // Gemini: requests (and optionally tokens) per Pacific day.
        var g = _opt.Gemini;
        var gToday = await SumKindsAsync(GeminiAnalysisProvider.ProviderName, new[] { AiProviderKind.DocumentAnalysis, AiProviderKind.Chat }, pacStart, spent, ct);
        var gMonth = await SumKindsAsync(GeminiAnalysisProvider.ProviderName, new[] { AiProviderKind.DocumentAnalysis, AiProviderKind.Chat }, monthStart, spent, ct);
        const string geminiNote = "No monthly limit is published; the daily limit renews every day.";
        lines.Add(Line("gemini", "requests", "requests", g.DailyRequestLimit, gToday.Requests, pacStart, pacNext, "midnight Pacific time", g.RequestsPerMinute, gMonth.Requests, geminiNote));
        if (g.DailyTokenLimit > 0)
            lines.Add(Line("gemini", "tokens", "tokens", g.DailyTokenLimit, gToday.Tokens, pacStart, pacNext, "midnight Pacific time", null, gMonth.Tokens, geminiNote));

        // Tamil chat (Gemini): its own caps inside the Gemini total, shown on their own so its requests and tokens can be watched separately.
        if (g.ChatDailyRequestLimit > 0)
        {
            var tToday = await SumKindsAsync(GeminiAnalysisProvider.ProviderName, new[] { AiProviderKind.Chat }, pacStart, spent, ct);
            var tMonth = await SumKindsAsync(GeminiAnalysisProvider.ProviderName, new[] { AiProviderKind.Chat }, monthStart, spent, ct);
            const string tamilNote = "Part of the Gemini total above. When this cap is reached Tamil answers use the default free chat model.";
            lines.Add(Line("gemini-chat", "requests", "requests", g.ChatDailyRequestLimit, tToday.Requests, pacStart, pacNext, "midnight Pacific time", g.RequestsPerMinute, tMonth.Requests, tamilNote));
            if (g.ChatDailyTokenLimit > 0)
                lines.Add(Line("gemini-chat", "tokens", "tokens", g.ChatDailyTokenLimit, tToday.Tokens, pacStart, pacNext, "midnight Pacific time", null, tMonth.Tokens, tamilNote));
        }
        // Groq: requests and tokens per UTC day. Tokens per day is usually the binding one.
        var q = _opt.Groq;
        var qToday = await SumAsync(GroqChatProvider.ProviderName, AiProviderKind.Chat, utcDay, spent, ct);
        var qMonth = await SumAsync(GroqChatProvider.ProviderName, AiProviderKind.Chat, monthStart, spent, ct);
        const string groqNote = "No monthly limit is published; the daily limits renew every day.";
        lines.Add(Line("groq", "requests", "requests", q.DailyRequestLimit, qToday.Requests, utcDay, nextUtc, "00:00 UTC", q.RequestsPerMinute, qMonth.Requests, groqNote));
        if (q.DailyTokenLimit > 0)
            lines.Add(Line("groq", "tokens", "tokens", q.DailyTokenLimit, qToday.Tokens, utcDay, nextUtc, "00:00 UTC", null, qMonth.Tokens, groqNote));

        // Cloudflare: neurons per UTC day.
        var cf = _opt.Cloudflare;
        var cToday = await SumAsync(CloudflareEmbeddingProvider.ProviderName, AiProviderKind.Embedding, utcDay, success, ct);
        var cMonth = await SumAsync(CloudflareEmbeddingProvider.ProviderName, AiProviderKind.Embedding, monthStart, success, ct);
        lines.Add(Line("cloudflare", "neurons", "neurons", cf.DailyNeuronLimit, Math.Round(cToday.Neurons, 2), utcDay, nextUtc, "00:00 UTC", null, Math.Round(cMonth.Neurons, 2),
            "No monthly limit; the free allowance is daily and renews at 00:00 UTC."));

        // Mistral OCR: pages per UTC day. Off unless the owner sets a limit.
        var m = _opt.MistralOcr;
        var mToday = await SumAsync(MistralOcrProvider.ProviderName, AiProviderKind.Ocr, utcDay, success, ct);
        var mMonth = await SumAsync(MistralOcrProvider.ProviderName, AiProviderKind.Ocr, monthStart, success, ct);
        lines.Add(Line("mistral", "pages", "pages", m.DailyPageLimit, mToday.Pages, utcDay, nextUtc, "00:00 UTC", null, mMonth.Pages,
            "Mistral's own free tier reportedly has a monthly token limit; check your Mistral Limits page."));

        return lines;
    }

    private Task<Sums> SumAsync(string provider, AiProviderKind kind, DateTime since, AiRequestStatus[] statuses, CancellationToken ct) => SumKindsAsync(provider, new[] { kind }, since, statuses, ct);

    private async Task<Sums> SumKindsAsync(string provider, AiProviderKind[] kinds, DateTime since, AiRequestStatus[] statuses, CancellationToken ct)
    {
        var rows = _db.AiUsageLogs.Where(l => l.Provider == provider && kinds.Contains(l.ProviderKind) && l.TimestampUtc >= since && statuses.Contains(l.Status));
        var agg = await rows.GroupBy(_ => 1).Select(x => new
        {
            C = x.Count(), T = x.Sum(l => (long)l.InputTokens + l.OutputTokens), N = x.Sum(l => l.EmbeddingUnits), P = x.Sum(l => (long)l.OcrPages)
        }).FirstOrDefaultAsync(ct);
        return agg == null ? new Sums(0, 0, 0, 0) : new Sums(agg.C, agg.T, (double)agg.N, agg.P);
    }
}
