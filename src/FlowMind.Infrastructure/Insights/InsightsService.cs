using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Infrastructure.Chat;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.Infrastructure.Insights;

/// <summary>Questions are shown to the admin without e-mail addresses and long numbers (phone, patient or ID numbers), and never with who asked.</summary>
public static class PersonalData
{
    private static readonly Regex Email = new(@"[\w.+\-]+@[\w\-]+(\.[\w\-]+)+", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"(?<!\w)\+?\d[\d\s\-]{5,}\d(?!\w)", RegexOptions.Compiled);
    private static readonly Regex DateLike = new(@"^\d{1,2}[-/ ]\d{1,2}[-/ ]\d{2,4}$", RegexOptions.Compiled);

    public static string Mask(string text)
    {
        text = Email.Replace(text, "[email]");
        return Number.Replace(text, m => m.Value.Count(char.IsDigit) >= 7 && !DateLike.IsMatch(m.Value.Trim()) ? "[number]" : m.Value);
    }
}

public sealed record GapCloseDoc(string Document, int Page, int Times);
/// <param name="State">"open", "handled" or "ignored". A question asked again after it was handled or ignored is open again (Reopened).</param>
public sealed record GapItem(string Key, string Question, int Count, DateTime LastAskedUtc, IReadOnlyList<string> Channels, IReadOnlyList<GapCloseDoc> Closest, string State, bool Reopened);
public sealed record GapReport(int Days, int Open, int Handled, int Ignored, IReadOnlyList<GapItem> Items);

public sealed record DayPoint(string Date, int Questions, int NotDocumented);
public sealed record TopQuestion(string Question, int Count, bool AllAnswered);
public sealed record ChannelRow(string Name, int Questions);
public sealed record UsageReport(
    string Application, DateTime FromUtc, DateTime ToUtc, int Days,
    int Questions, int Answered, int NotDocumented, int AnswerRatePercent, int PreviousQuestions,
    int Conversations, int People, int ActiveDays, int TamilQuestions,
    IReadOnlyList<DayPoint> Daily, IReadOnlyList<TopQuestion> TopQuestions, IReadOnlyList<ChannelRow> Channels,
    int SupportReceived, int SupportClosed, int SupportOpenNow, int OpenGaps, int DocumentsChanged, bool Truncated);

/// <summary>
/// Turns the chats the assistant already stores into two things the admin can act on: the questions it could not answer (so the
/// documentation can be fixed) and a period report (so the value of the assistant can be shown). Everything is computed from the stored
/// chats, so a chat that is deleted (by the visitor or by the admin) also disappears from these numbers. Nothing here calls an AI provider.
/// </summary>
public sealed class InsightsService
{
    public const int MaxDays = 90, MaxExchanges = 20000;
    private readonly FlowMindDbContext _db;
    private readonly ITenantContext _tenant;
    public InsightsService(FlowMindDbContext db, ITenantContext tenant) { _db = db; _tenant = tenant; }

    private sealed record Exchange(Guid ConversationId, string Owner, string Question, bool Grounded, DateTime Utc, string? CitationsJson);

    // ---- keys and display text --------------------------------------------------------------------------------------

    private static string Shown(string message)
    {
        var c = RequestCondenser.Condense(message, 300).Text;
        var text = PersonalData.Mask(c.Length > 0 ? c : (message ?? "").Trim());
        return text.Length > 300 ? text[..300].TrimEnd() + "…" : text;
    }

    /// <summary>Same question, different wording of case/punctuation/number: one key. Only this hash is ever stored, never the text.</summary>
    public static string KeyOf(string message)
    {
        var text = Shown(message);
        var norm = Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{M}\p{N}]+", " ").Trim();
        if (norm.Length == 0) norm = text;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm))).ToLowerInvariant();
    }

    // ---- loading ----------------------------------------------------------------------------------------------------

    /// <summary>Each stored answer paired with the question that came before it, for one application. Greetings and thanks are left out.</summary>
    private async Task<(List<Exchange> Items, bool Truncated)> LoadAsync(Guid applicationId, DateTime since, DateTime until, CancellationToken ct)
    {
        var rows = await (from m in _db.ConversationMessages
                          join c in _db.Conversations on m.ConversationId equals c.Id
                          where m.Role == "assistant" && m.CreatedUtc >= since && m.CreatedUtc < until && c.ApplicationId == applicationId
                          orderby m.Id descending
                          select new { m.Id, m.ConversationId, m.Grounded, m.CreatedUtc, m.CitationsJson, c.OwnerId }).Take(MaxExchanges + 1).ToListAsync(ct);
        var truncated = rows.Count > MaxExchanges;
        if (truncated) rows = rows.Take(MaxExchanges).ToList();

        var users = new Dictionary<Guid, List<(long Id, string Content)>>();
        foreach (var batch in rows.Select(r => r.ConversationId).Distinct().Chunk(500))
        {
            var part = batch.ToList();
            var found = await _db.ConversationMessages.Where(m => m.Role == "user" && part.Contains(m.ConversationId))
                .Select(m => new { m.Id, m.ConversationId, m.Content }).ToListAsync(ct);
            foreach (var g in found.GroupBy(x => x.ConversationId)) users[g.Key] = g.OrderBy(x => x.Id).Select(x => (x.Id, x.Content)).ToList();
        }

        var items = new List<Exchange>();
        foreach (var r in rows)
        {
            if (!users.TryGetValue(r.ConversationId, out var list)) continue;
            var q = list.LastOrDefault(u => u.Id < r.Id);
            if (q.Content == null || SmallTalk.TryReply(q.Content, "assistant") != null) continue;   // "hi", "thanks": not a question
            items.Add(new Exchange(r.ConversationId, r.OwnerId, q.Content, r.Grounded, r.CreatedUtc, r.CitationsJson));
        }
        return (items, truncated);
    }

    /// <summary>The wording shown for a group of the same question: the newest one that is not typed in capitals (so "HOW DO I..." never heads the list).</summary>
    private static Exchange Wording(IEnumerable<Exchange> group)
    {
        var newest = group.OrderByDescending(x => x.Utc).ToList();
        return newest.FirstOrDefault(x => x.Question != x.Question.ToUpperInvariant()) ?? newest[0];
    }

    private async Task<Dictionary<Guid, string>> LinkNamesAsync(CancellationToken ct) => await _db.BotLinks.ToDictionaryAsync(l => l.Id, l => l.Name, ct);

    private static string Channel(string owner, IReadOnlyDictionary<Guid, string> links)
    {
        if (owner.StartsWith("bot:", StringComparison.Ordinal) && owner.Length >= 36 && Guid.TryParseExact(owner.AsSpan(4, 32), "N", out var id))
            return links.TryGetValue(id, out var name) ? name : "Share link (removed)";
        return "Signed-in users";
    }

    private static IEnumerable<(string Document, int Page)> Closest(string? json)
    {
        if (string.IsNullOrEmpty(json)) yield break;
        List<Citation>? list = null;
        try { list = JsonSerializer.Deserialize<List<Citation>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); } catch (JsonException) { /* none */ }
        foreach (var c in list ?? new()) yield return (c.Document, c.Page);
    }

    // ---- unanswered questions ---------------------------------------------------------------------------------------

    public async Task<GapReport> GapsAsync(Guid applicationId, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, MaxDays);
        var now = DateTime.UtcNow;
        var (items, _) = await LoadAsync(applicationId, now.AddDays(-days), now.AddMinutes(1), ct);
        return await BuildGapsAsync(applicationId, days, items, ct);
    }

    private async Task<GapReport> BuildGapsAsync(Guid applicationId, int days, List<Exchange> items, CancellationToken ct)
    {
        var links = await LinkNamesAsync(ct);
        var status = await _db.QuestionGapStatuses.Where(s => s.ApplicationId == applicationId).ToDictionaryAsync(s => s.Key, ct);
        var result = new List<GapItem>();
        foreach (var g in items.Where(i => !i.Grounded).GroupBy(i => KeyOf(i.Question)))
        {
            // Only questions about something the documents come close to are gaps. A "not documented" reply that found no near section was about
            // something else entirely (a song, a stray word), so it is not listed. Older replies carry the same marker: no closest sections stored.
            if (!g.Any(x => Closest(x.CitationsJson).Any())) continue;
            var latest = g.OrderByDescending(x => x.Utc).First();
            var last = latest.Utc;
            var st = status.GetValueOrDefault(g.Key);
            var reopened = st != null && last > st.UpdatedUtc;
            var state = st == null || reopened ? "open" : st.Status;
            var close = g.SelectMany(x => Closest(x.CitationsJson)).GroupBy(c => c).OrderByDescending(c => c.Count()).Take(3)
                .Select(c => new GapCloseDoc(c.Key.Document, c.Key.Page, c.Count())).ToList();
            result.Add(new GapItem(g.Key, Shown(Wording(g).Question), g.Count(), DateTime.SpecifyKind(last, DateTimeKind.Utc),
                g.Select(x => Channel(x.Owner, links)).Distinct().OrderBy(x => x).Take(4).ToList(), close, state, reopened));
        }
        var ordered = result.OrderBy(r => r.State == "open" ? 0 : 1).ThenByDescending(r => r.Count).ThenByDescending(r => r.LastAskedUtc).Take(200).ToList();
        return new GapReport(days, result.Count(r => r.State == "open"), result.Count(r => r.State == "handled"), result.Count(r => r.State == "ignored"), ordered);
    }

    /// <summary>Record what the admin decided. "open" forgets an earlier decision. Returns false for a bad key or status.</summary>
    public async Task<bool> SetGapStateAsync(Guid applicationId, string key, string status, CancellationToken ct)
    {
        if (key == null || !Regex.IsMatch(key, "^[0-9a-f]{64}$") || status is not ("handled" or "ignored" or "open")) return false;
        var row = await _db.QuestionGapStatuses.FirstOrDefaultAsync(s => s.ApplicationId == applicationId && s.Key == key, ct);
        if (status == "open") { if (row != null) _db.QuestionGapStatuses.Remove(row); }
        else if (row == null) _db.QuestionGapStatuses.Add(new QuestionGapStatus { TenantId = _tenant.TenantId!.Value, ApplicationId = applicationId, Key = key, Status = status });
        else { row.Status = status; row.UpdatedUtc = DateTime.UtcNow; }
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ---- period report ----------------------------------------------------------------------------------------------

    public async Task<UsageReport?> UsageAsync(Guid applicationId, int days, CancellationToken ct)
    {
        var appName = await _db.Applications.Where(a => a.Id == applicationId).Select(a => a.Name).FirstOrDefaultAsync(ct);
        if (appName == null) return null;
        days = Math.Clamp(days, 1, MaxDays);
        var to = DateTime.UtcNow.AddMinutes(1);
        var from = DateTime.UtcNow.AddDays(-days);
        var (all, truncated) = await LoadAsync(applicationId, from.AddDays(-days), to, ct);   // this period and the one before it, for the comparison
        var now = all.Where(i => i.Utc >= from).ToList();
        var links = await LinkNamesAsync(ct);

        var answered = now.Count(i => i.Grounded);
        var daily = Enumerable.Range(0, days).Select(d => DateTime.UtcNow.Date.AddDays(-(days - 1 - d))).Select(day =>
        {
            var of = now.Where(i => i.Utc.Date == day).ToList();
            return new DayPoint(day.ToString("yyyy-MM-dd"), of.Count, of.Count(i => !i.Grounded));
        }).ToList();

        var top = now.GroupBy(i => KeyOf(i.Question)).Select(g => new { Latest = g.OrderByDescending(x => x.Utc).First(), Shown = Wording(g), Count = g.Count(), AllOk = g.All(x => x.Grounded) })
            .OrderByDescending(x => x.Count).ThenByDescending(x => x.Latest.Utc).Take(10).Select(x => new TopQuestion(Shown(x.Shown.Question), x.Count, x.AllOk)).ToList();
        var channels = now.GroupBy(i => Channel(i.Owner, links)).Select(g => new ChannelRow(g.Key, g.Count())).OrderByDescending(c => c.Questions).ToList();

        var support = await _db.SupportRequests.Where(s => s.ApplicationId == applicationId).Select(s => new { s.CreatedUtc, s.ClosedUtc, s.Status }).ToListAsync(ct);
        var gaps = await BuildGapsAsync(applicationId, days, now, ct);

        var docIds = (await _db.Documents.Where(d => d.ApplicationId == applicationId).Select(d => d.Id).ToListAsync(ct)).Select(d => d.ToString()).ToHashSet();
        var tenantId = _tenant.TenantId;
        var changed = (await _db.AuditLogs.Where(a => a.TenantId == tenantId && a.TimestampUtc >= from && a.TimestampUtc < to && (a.Action == "document.upload" || a.Action == "document.update"))
            .Select(a => a.EntityId).ToListAsync(ct)).Count(id => id != null && docIds.Contains(id));

        return new UsageReport(appName, DateTime.SpecifyKind(from, DateTimeKind.Utc), DateTime.SpecifyKind(to, DateTimeKind.Utc), days,
            now.Count, answered, now.Count - answered, now.Count == 0 ? 0 : (int)Math.Round(100.0 * answered / now.Count), all.Count - now.Count,
            now.Select(i => i.ConversationId).Distinct().Count(), now.Select(i => i.Owner).Distinct().Count(), daily.Count(d => d.Questions > 0), now.Count(i => Language.HasTamil(i.Question)),
            daily, top, channels,
            support.Count(s => s.CreatedUtc >= from), support.Count(s => s.ClosedUtc >= from), support.Count(s => s.Status == "open"), gaps.Open, changed, truncated);
    }
}

