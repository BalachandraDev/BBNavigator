using FlowMind.Infrastructure.Security;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FlowMind.API.Authentication;
using FlowMind.API.Tenants;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Chat;
using FlowMind.Infrastructure.Database;
using FlowMind.Infrastructure.Insights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.API.Controllers;

public sealed class BotLinkRequest
{
    [Required, StringLength(200, MinimumLength = 2)] public string Name { get; set; } = "";
    [Required, StringLength(120, MinimumLength = 2)] public string Title { get; set; } = "";
    public Guid? ApplicationId { get; set; }
    [StringLength(500)] public string? Welcome { get; set; }
    public List<string>? Starters { get; set; }
    [Range(1, 100000)] public int DailyMessageLimit { get; set; } = 300;
    /// <summary>Documents (modules) this link may answer from. Empty or null = all approved documents of the application.</summary>
    public List<Guid>? DocumentIds { get; set; }
    /// <summary>"#rrggbb" accent colour of the chat page.</summary>
    [RegularExpression("^(#[0-9a-fA-F]{6})?$", ErrorMessage = "Accent colour must look like #3752d6.")] public string? AccentColor { get; set; }
    /// <summary>Look and behaviour of the visitor page.</summary>
    public BotTheme? Theme { get; set; }
    /// <summary>Audience whose access rules limit this link. Omit for no audience limit.</summary>
    public Guid? AudienceId { get; set; }
}
public sealed class BotLinkPatch
{
    public bool? Active { get; set; }
    [StringLength(120, MinimumLength = 2)] public string? Title { get; set; }
    [StringLength(500)] public string? Welcome { get; set; }
    public List<string>? Starters { get; set; }
    [Range(1, 100000)] public int? DailyMessageLimit { get; set; }
    public List<Guid>? DocumentIds { get; set; }
    [RegularExpression("^(#[0-9a-fA-F]{6})?$", ErrorMessage = "Accent colour must look like #3752d6.")] public string? AccentColor { get; set; }
    public BotTheme? Theme { get; set; }
    /// <summary>Set an audience for the link.</summary>
    public Guid? AudienceId { get; set; }
    /// <summary>True removes the audience limit from the link.</summary>
    public bool? NoAudience { get; set; }
}
/// <param name="Token">The secret in the share link. Null when it cannot be recovered (server secret changed): create a new link.</param>
public sealed record BotLinkSummary(Guid Id, string Name, string Title, string Application, bool Active, int DailyMessageLimit, int TodayMessages,
    DateTime CreatedUtc, DateTime? LastUsedUtc, string? Token, string? Welcome, IReadOnlyList<string> Starters, string? AccentColor, IReadOnlyList<Guid> DocumentIds, IReadOnlyList<string> DocumentNames, BotTheme Theme, Guid ApplicationId)
{
    /// <summary>How many visitor chats are stored for this link (the admin sees only the count, never the content).</summary>
    public int StoredChats { get; init; }
    /// <summary>The audience that limits what this link can answer from. Null = no audience limit.</summary>
    public Guid? AudienceId { get; init; }
    public string? AudienceName { get; init; }
}

/// <param name="Questions">Visitor questions still stored for this link in the period (a visitor deleting a chat, or retention, removes them), so use AI calls for exact cost.</param>
/// <param name="AiCalls">Chat answers that really called the AI model. Answers served from cache, greetings and "what can I ask" replies make no call.</param>
public sealed record LinkUsageRow(Guid? LinkId, string Name, bool Exists, bool Active, int Questions, int AiCalls, long InputTokens, long OutputTokens, decimal EmbeddingUnits, int CachedAnswers, int Failed)
{
    public long TotalTokens => InputTokens + OutputTokens;
    /// <summary>Average tokens per AI call, so a link with long questions or long answers stands out.</summary>
    public long TokensPerCall => AiCalls == 0 ? 0 : TotalTokens / AiCalls;
    /// <summary>This link's share of all chat tokens this company used in the period.</summary>
    public int SharePercent { get; init; }
}
/// <param name="OtherTokens">Chat tokens not caused by a share link (the admin's Assistant page, access keys, API calls).</param>
public sealed record LinkUsageReport(int Days, IReadOnlyList<LinkUsageRow> Links, long OtherTokens, long TotalTokens);

internal static class BotText
{
    public static List<string> Starters(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<List<string>>(json) ?? new(); } catch { return new(); }
    }
    public static string? PackStarters(IEnumerable<string>? s)
    {
        var list = (s ?? Enumerable.Empty<string>()).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Select(x => x.Length > 120 ? x[..120] : x).Take(4).ToList();
        return list.Count == 0 ? null : JsonSerializer.Serialize(list);
    }
    public static List<Guid> DocIds(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<List<Guid>>(json) ?? new(); } catch { return new(); }
    }
    public static string Owner(Guid linkId) => $"bot:{linkId:N}:";
}

/// <summary>Company admins create, adjust, disable and delete chat-only share links.</summary>
[ApiController]
[Route("api/v1/bot-links")]
[Authorize(Policy = "TenantAdmin")]
public class BotLinksController : ControllerBase
{
    private const int MaxLinks = 20;
    private readonly FlowMindDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IAuditLogger _audit;
    private readonly SecretBox _box;
    public BotLinksController(FlowMindDbContext db, ITenantContext tenant, IAuditLogger audit, SecretBox box) { _db = db; _tenant = tenant; _audit = audit; _box = box; }

    [HttpGet]
    public async Task<IReadOnlyList<BotLinkSummary>> List(CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var links = await _db.BotLinks.OrderByDescending(l => l.CreatedUtc).ToListAsync(ct);
        var apps = await _db.Applications.ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var docNames = await _db.Documents.ToDictionaryAsync(d => d.Id, d => d.FileName, ct);
        var audiences = await AudienceNames(ct);
        var result = new List<BotLinkSummary>();
        foreach (var l in links)
        {
            var prefix = BotText.Owner(l.Id);
            var used = await Today(prefix, today, ct);
            var chats = await _db.Conversations.CountAsync(c => c.OwnerId.StartsWith(prefix), ct);
            result.Add(Summary(l, apps.GetValueOrDefault(l.ApplicationId, "(removed)"), used, docNames, audiences) with { StoredChats = chats });
        }
        return result;
    }

    /// <summary>
    /// How much AI each share link has used over the last N days. Counted from the usage log, where every AI call caused by a link's visitor is stamped
    /// with that link (chat answer, search embedding, retries). The tenant filter is explicit because the usage log is also used for server-wide limits.
    /// </summary>
    [HttpGet("usage")]
    public async Task<LinkUsageReport> Usage([FromQuery] int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 90);
        var tenantId = _tenant.TenantId!.Value;
        var since = DateTime.UtcNow.AddDays(-days);

        var groups = await _db.AiUsageLogs.Where(l => l.TenantId == tenantId && l.TimestampUtc >= since
                && (l.ProviderKind == AiProviderKind.Chat || l.ProviderKind == AiProviderKind.Embedding))
            .GroupBy(l => l.BotLinkId)
            .Select(g => new
            {
                Link = g.Key,
                AiCalls = g.Count(x => x.ProviderKind == AiProviderKind.Chat && x.Status == AiRequestStatus.Success),
                In = g.Sum(x => x.ProviderKind == AiProviderKind.Chat ? (long)x.InputTokens : 0L), Out = g.Sum(x => x.ProviderKind == AiProviderKind.Chat ? (long)x.OutputTokens : 0L),   // chat model tokens only; the search embedding is its own allowance (neurons)
                Emb = g.Sum(x => x.EmbeddingUnits),
                Cached = g.Count(x => x.Status == AiRequestStatus.Cached),
                Failed = g.Count(x => x.Status == AiRequestStatus.Failed || x.Status == AiRequestStatus.RateLimited || x.Status == AiRequestStatus.QuotaExceeded),
            }).ToListAsync(ct);
        var by = groups.ToDictionary(g => g.Link ?? Guid.Empty);

        var links = await _db.BotLinks.ToListAsync(ct);
        var rows = new List<LinkUsageRow>();
        foreach (var l in links)
        {
            var prefix = BotText.Owner(l.Id);
            var questions = await (from m in _db.ConversationMessages join c in _db.Conversations on m.ConversationId equals c.Id
                                   where m.Role == "user" && m.CreatedUtc >= since && c.OwnerId.StartsWith(prefix) select m.Id).CountAsync(ct);
            by.TryGetValue(l.Id, out var g);
            rows.Add(new LinkUsageRow(l.Id, l.Name, true, l.IsActive, questions, g?.AiCalls ?? 0, g?.In ?? 0, g?.Out ?? 0, g?.Emb ?? 0, g?.Cached ?? 0, g?.Failed ?? 0));
        }
        // Links that were deleted keep their history, so the totals still add up.
        foreach (var g in groups.Where(g => g.Link != null && links.All(l => l.Id != g.Link)))
            rows.Add(new LinkUsageRow(g.Link, "(deleted link)", false, false, 0, g.AiCalls, g.In, g.Out, g.Emb, g.Cached, g.Failed));

        var other = by.GetValueOrDefault(Guid.Empty);   // admin chat, access keys, document reading: not caused by a share link
        var tokensOther = (other?.In ?? 0) + (other?.Out ?? 0);
        var tokensLinks = rows.Sum(r => r.InputTokens + r.OutputTokens);
        var total = Math.Max(1, tokensLinks + tokensOther);
        rows = rows.Select(r => r with { SharePercent = (int)Math.Round(100.0 * (r.InputTokens + r.OutputTokens) / total) })
            .OrderByDescending(r => r.InputTokens + r.OutputTokens).ThenBy(r => r.Name).ToList();
        return new LinkUsageReport(days, rows, tokensOther, tokensLinks + tokensOther);
    }

    [HttpPost]
    public async Task<ActionResult<BotLinkSummary>> Create(BotLinkRequest req, CancellationToken ct)
    {
        if (await _db.BotLinks.CountAsync(l => l.IsActive, ct) >= MaxLinks) return Problem($"Limit of {MaxLinks} active links reached. Delete unused links first.", statusCode: 409);
        if (!_box.Enabled) return Problem("Share links need a server secret (Security:SecretsKey) so the link can be shown again later.", statusCode: 409);

        var apps = await _db.Applications.OrderBy(a => a.Name).ToListAsync(ct);
        var app = req.ApplicationId is { } id ? apps.FirstOrDefault(a => a.Id == id) : (apps.Count == 1 ? apps[0] : null);
        if (app == null) return Problem(apps.Count == 0 ? "Create an application first." : "Choose which application this link answers about.", statusCode: 400);

        var scope = await ValidDocs(app.Id, req.DocumentIds, ct);
        if (scope == null) return Problem("One or more chosen documents do not belong to this application.", statusCode: 400);
        if (req.AudienceId is { } wantedAudience && !await _db.Audiences.AnyAsync(a => a.Id == wantedAudience, ct)) return Problem("That audience does not exist.", statusCode: 400);
        var token = ApiKeys.Generate();
        var e = new BotLink
        {
            TenantId = _tenant.TenantId!.Value, ApplicationId = app.Id, Name = req.Name.Trim(), Title = req.Title.Trim(),
            Welcome = string.IsNullOrWhiteSpace(req.Welcome) ? null : req.Welcome.Trim(), StartersJson = BotText.PackStarters(req.Starters),
            TokenHash = ApiKeys.Hash(token), TokenCipher = _box.Encrypt(token), DailyMessageLimit = req.DailyMessageLimit,
            AccentColor = string.IsNullOrWhiteSpace(req.AccentColor) ? null : req.AccentColor.ToLowerInvariant(),
            DocumentIdsJson = scope.Count > 0 ? JsonSerializer.Serialize(scope) : null,
            ThemeJson = req.Theme?.Pack(),
            AudienceId = req.AudienceId
        };
        _db.BotLinks.Add(e);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("botlink.create", "BotLink", e.Id.ToString(), e.Name, ct: ct);
        return Created($"/api/v1/bot-links/{e.Id}", Summary(e, app.Name, 0, await _db.Documents.ToDictionaryAsync(d => d.Id, d => d.FileName, ct), await AudienceNames(ct)));
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<BotLinkSummary>> Update(Guid id, BotLinkPatch p, CancellationToken ct)
    {
        var l = await _db.BotLinks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l == null) return NotFound();
        if (p.Active != null) l.IsActive = p.Active.Value;
        if (p.Title != null) l.Title = p.Title.Trim();
        if (p.Welcome != null) l.Welcome = string.IsNullOrWhiteSpace(p.Welcome) ? null : p.Welcome.Trim();
        if (p.Starters != null) l.StartersJson = BotText.PackStarters(p.Starters);
        if (p.DailyMessageLimit != null) l.DailyMessageLimit = p.DailyMessageLimit.Value;
        if (p.AccentColor != null) l.AccentColor = string.IsNullOrWhiteSpace(p.AccentColor) ? null : p.AccentColor.ToLowerInvariant();
        if (p.Theme != null) l.ThemeJson = p.Theme.Pack();
        var audienceChanged = false;
        if (p.NoAudience == true) { audienceChanged = l.AudienceId != null; l.AudienceId = null; }
        else if (p.AudienceId is { } newAudience)
        {
            if (!await _db.Audiences.AnyAsync(a => a.Id == newAudience, ct)) return Problem("That audience does not exist.", statusCode: 400);
            audienceChanged = l.AudienceId != newAudience; l.AudienceId = newAudience;
        }
        if (p.DocumentIds != null)
        {
            var scope = await ValidDocs(l.ApplicationId, p.DocumentIds, ct);
            if (scope == null) return Problem("One or more chosen documents do not belong to this application.", statusCode: 400);
            l.DocumentIdsJson = scope.Count > 0 ? JsonSerializer.Serialize(scope) : null;
        }
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(p.Active == false ? "botlink.disable" : p.Active == true ? "botlink.enable" : "botlink.update", "BotLink", l.Id.ToString(), l.Name, ct: ct);
        if (audienceChanged) await _audit.LogAsync("botlink.audience", "BotLink", l.Id.ToString(), l.AudienceId == null ? $"{l.Name}: no audience limit" : l.Name, ct: ct);
        var app = await _db.Applications.Where(a => a.Id == l.ApplicationId).Select(a => a.Name).FirstOrDefaultAsync(ct) ?? "(removed)";
        return Summary(l, app, await Today(BotText.Owner(l.Id), DateTime.UtcNow.Date, ct), await _db.Documents.ToDictionaryAsync(d => d.Id, d => d.FileName, ct), await AudienceNames(ct));
    }

    /// <summary>Delete the link for good: the address stops working immediately.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var l = await _db.BotLinks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l == null) return NotFound();
        var erased = await EraseChatsAsync(l.Id, ct);   // the visitors' chats go with the link, nothing is left behind
        _db.BotLinks.Remove(l);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("botlink.delete", "BotLink", id.ToString(), $"{l.Name} ({erased} chats erased)", ct: ct);
        return NoContent();
    }

    /// <summary>Erase every visitor chat stored for this link (all visitors), keeping the link itself. Returns how many chats were erased.</summary>
    [HttpDelete("{id:guid}/conversations")]
    public async Task<ActionResult<object>> ClearHistory(Guid id, CancellationToken ct)
    {
        var l = await _db.BotLinks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l == null) return NotFound();
        var erased = await EraseChatsAsync(l.Id, ct);
        await _audit.LogAsync("botlink.clear-history", "BotLink", l.Id.ToString(), $"{l.Name} ({erased} chats erased)", ct: ct);
        return Ok(new { erased });
    }

    private async Task<int> EraseChatsAsync(Guid linkId, CancellationToken ct)
    {
        var prefix = BotText.Owner(linkId);
        var ids = await _db.Conversations.Where(c => c.OwnerId.StartsWith(prefix)).Select(c => c.Id).ToListAsync(ct);
        foreach (var batch in ids.Chunk(500))   // batches keep the SQL parameter count small for links with many chats
        {
            var part = batch.ToList();
            _db.ConversationMessages.RemoveRange(await _db.ConversationMessages.Where(m => part.Contains(m.ConversationId)).ToListAsync(ct));
            _db.Conversations.RemoveRange(await _db.Conversations.Where(c => part.Contains(c.Id)).ToListAsync(ct));
            await _db.SaveChangesAsync(ct);
        }
        return ids.Count;
    }

    private Task<int> Today(string ownerPrefix, DateTime today, CancellationToken ct) =>
        (from m in _db.ConversationMessages join c in _db.Conversations on m.ConversationId equals c.Id
         where m.Role == "user" && m.CreatedUtc >= today && c.OwnerId.StartsWith(ownerPrefix) select m.Id).CountAsync(ct);

    /// <summary>The chosen documents if all are live documents of this application; null when any is not. Empty = no restriction.</summary>
    private async Task<List<Guid>?> ValidDocs(Guid applicationId, List<Guid>? ids, CancellationToken ct)
    {
        var wanted = (ids ?? new()).Distinct().ToList();
        if (wanted.Count == 0) return new();
        var ok = await _db.Documents.Where(d => d.ApplicationId == applicationId && wanted.Contains(d.Id)).Select(d => d.Id).ToListAsync(ct);
        return ok.Count == wanted.Count ? wanted : null;
    }

    private BotLinkSummary Summary(BotLink l, string app, int used, IReadOnlyDictionary<Guid, string> docNames, IReadOnlyDictionary<Guid, string> audiences)
    {
        var ids = BotText.DocIds(l.DocumentIdsJson);
        return new BotLinkSummary(l.Id, l.Name, l.Title, app, l.IsActive, l.DailyMessageLimit, used, l.CreatedUtc, l.LastUsedUtc, _box.TryDecrypt(l.TokenCipher), l.Welcome,
            BotText.Starters(l.StartersJson), l.AccentColor, ids, ids.Select(i => docNames.GetValueOrDefault(i, "(removed)")).ToList(), BotTheme.Read(l.ThemeJson), l.ApplicationId)
        {
            AudienceId = l.AudienceId,
            AudienceName = l.AudienceId is { } a ? audiences.GetValueOrDefault(a, "(removed)") : null
        };
    }

    private async Task<Dictionary<Guid, string>> AudienceNames(CancellationToken ct) => await _db.Audiences.ToDictionaryAsync(a => a.Id, a => a.Name, ct);
}

public sealed class BotAskRequest
{
    [Required, StringLength(8000, MinimumLength = 2)] public string Message { get; set; } = "";
    public Guid? ConversationId { get; set; }
    /// <summary>The language the visitor selected on the page ("en" or "ta"). The answer is written in it.</summary>
    [RegularExpression("^(en|ta)?$")] public string? Language { get; set; }
}
public sealed class BotSupportRequest
{
    [Required] public Guid ConversationId { get; set; }
    /// <summary>What the visitor was trying to do (optional).</summary>
    [StringLength(500)] public string? Note { get; set; }
    /// <summary>How the company can reach the visitor (optional): a name, phone number or e-mail they choose to give.</summary>
    [StringLength(120)] public string? Contact { get; set; }
}
public sealed record BotInfo(string Title, string? Welcome, IReadOnlyList<string> Starters, string? AccentColor, BotTheme Theme);
public sealed record BotChatSummary(Guid Id, string Title, DateTime UpdatedUtc);
public sealed record BotChatMessage(string Role, string Content, bool Documented, DateTime CreatedUtc);
/// <summary>What a visitor gets back: the answer text only. No document names, pages or company details.</summary>
public sealed record BotAnswer(string Answer, bool Documented, Guid ConversationId);

/// <summary>
/// The public side of a share link. No sign-in: the secret in the link (header X-Bot-Token) selects ONE application of ONE company,
/// and the server, not the browser, decides the tenant. Cannot reach any other API.
/// </summary>
[ApiController]
[Route("api/bot")]
[AllowAnonymous]
public class BotController : ControllerBase
{
    private readonly FlowMindDbContext _db;
    private readonly ChatService _chat;
    private readonly ILogger<BotController> _log;
    private readonly SupportService _support;
    private readonly FlowMind.Infrastructure.Access.AccessResolver _access;
    private readonly UsageAttribution _who;
    public BotController(FlowMindDbContext db, ChatService chat, ILogger<BotController> log, SupportService support, FlowMind.Infrastructure.Access.AccessResolver access, UsageAttribution who)
    { _db = db; _chat = chat; _log = log; _support = support; _access = access; _who = who; }

    [HttpGet("info")]
    [EnableRateLimiting("bot")]
    public async Task<ActionResult<BotInfo>> Info(CancellationToken ct)
    {
        var l = await Resolve(ct);
        return l == null ? NotFound() : new BotInfo(l.Title, l.Welcome, BotText.Starters(l.StartersJson), l.AccentColor, BotTheme.Read(l.ThemeJson));
    }

    [HttpPost("ask")]
    [EnableRateLimiting("bot")]
    public async Task<ActionResult<BotAnswer>> Ask(BotAskRequest req, CancellationToken ct)
    {
        var l = await Resolve(ct);
        if (l == null) return NotFound();

        var visitor = Request.Headers["X-Bot-Visitor"].ToString();
        if (visitor.Length is < 8 or > 40 || !visitor.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')) visitor = "anonymous";
        var owner = BotText.Owner(l.Id);

        var today = DateTime.UtcNow.Date;
        var used = await (from m in _db.ConversationMessages join c in _db.Conversations on m.ConversationId equals c.Id
                          where m.Role == "user" && m.CreatedUtc >= today && c.OwnerId.StartsWith(owner) select m.Id).CountAsync(ct);
        if (used >= l.DailyMessageLimit) return Problem("This assistant has reached its daily limit. Please try again tomorrow.", statusCode: 429);

        _who.BotLinkId = l.Id;   // from the validated link, never from the visitor: every AI call this question causes is counted for this link
        try
        {
            var scope = BotText.DocIds(l.DocumentIdsJson);
            // A link with an audience answers only from what that audience's rules open (on top of any document limit above). The audience comes from the link, never from the visitor.
            var access = await _access.ForAudienceAsync(l.TenantId, l.AudienceId, ct);
            var a = await _chat.AskAsync(l.TenantId, owner + visitor, l.ApplicationId, req.ConversationId, req.Message, ct, scope.Count > 0 ? scope : null, req.Language, access);
            return new BotAnswer(a.Answer, a.Grounded, a.ConversationId);
        }
        catch (ConversationNotFoundException) { return Problem("That conversation was not found. Start a new chat.", statusCode: 404); }
        catch (QuotaExceededException) { return Problem("The assistant is very busy right now. Please try again later.", statusCode: 429); }
        catch (ProviderNotConfiguredException ex) { _log.LogWarning(ex, "AI provider not configured for bot"); return Problem("The assistant is unavailable right now.", statusCode: 503); }
        catch (Exception ex) when (ex is TransientProviderException or InvalidAnalysisOutputException)
        { _log.LogWarning(ex, "AI provider problem for bot"); return Problem("The assistant could not answer just now. Please try again.", statusCode: 503); }
    }

    /// <summary>
    /// "Send this to support": the visitor asks the company's people to follow up on the last question of one of their own chats.
    /// The question is read from that chat on the server. The company admin sees it in the support inbox.
    /// </summary>
    [HttpPost("support")]
    [EnableRateLimiting("support")]
    public async Task<IActionResult> Support(BotSupportRequest req, CancellationToken ct)
    {
        var l = await Resolve(ct);
        if (l == null) return NotFound();
        var owner = HistoryOwner(l);
        if (owner == null) return Problem("This browser cannot send a request. Please reload the page and try again.", statusCode: 400);
        try
        {
            await _support.CreateAsync(l.TenantId, l.Name, owner, req.ConversationId, req.Note, req.Contact, ct);
            return Ok(new { sent = true });
        }
        catch (SupportRejectedException ex) { return Problem(ex.Message, statusCode: ex.StatusCode); }
    }

    // ---- A visitor's own earlier chats. "Own" = this link + this browser's visitor id; nobody else's chats are ever listed. ----

    /// <summary>The caller's chat owner id, or null when the visitor id is missing or malformed (then there is no private history to show).</summary>
    private string? HistoryOwner(BotLink l)
    {
        var visitor = Request.Headers["X-Bot-Visitor"].ToString();
        if (visitor.Length is < 8 or > 40 || !visitor.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')) return null;
        return BotText.Owner(l.Id) + visitor;
    }

    [HttpGet("conversations")]
    [EnableRateLimiting("bot-read")]
    public async Task<ActionResult<IReadOnlyList<BotChatSummary>>> Chats(CancellationToken ct)
    {
        var l = await Resolve(ct);
        if (l == null) return NotFound();
        var theme = BotTheme.Read(l.ThemeJson);
        var owner = HistoryOwner(l);
        if (owner == null || !theme.HistoryEnabled) return new List<BotChatSummary>();

        // Retention: chats not touched for HistoryDays are removed for good (this visitor's only, on each visit).
        var cutoff = DateTime.UtcNow.AddDays(-theme.HistoryDays);
        var old = await _db.Conversations.Where(c => c.OwnerId == owner && c.UpdatedUtc < cutoff).Select(c => c.Id).ToListAsync(ct);
        if (old.Count > 0) await Erase(old, ct);

        return await _db.Conversations.Where(c => c.OwnerId == owner && c.ApplicationId == l.ApplicationId)
            .OrderByDescending(c => c.UpdatedUtc).Take(50).Select(c => new BotChatSummary(c.Id, c.Title, c.UpdatedUtc)).ToListAsync(ct);
    }

    [HttpGet("conversations/{id:guid}")]
    [EnableRateLimiting("bot-read")]
    public async Task<ActionResult<IReadOnlyList<BotChatMessage>>> Chat(Guid id, CancellationToken ct)
    {
        var l = await Resolve(ct);
        var owner = l == null ? null : HistoryOwner(l);
        if (l == null || owner == null || !BotTheme.Read(l.ThemeJson).HistoryEnabled) return NotFound();
        if (!await _db.Conversations.AnyAsync(c => c.Id == id && c.OwnerId == owner, ct)) return NotFound();
        return await _db.ConversationMessages.Where(m => m.ConversationId == id).OrderBy(m => m.CreatedUtc)
            .Select(m => new BotChatMessage(m.Role, m.Content, m.Grounded, m.CreatedUtc)).ToListAsync(ct);
    }

    /// <summary>The visitor deletes one of their own chats.</summary>
    // POST twins exist because many reverse proxies (IIS with WebDAV, some gateways) reject DELETE with 405; the page uses POST.
    [HttpDelete("conversations/{id:guid}")]
    [HttpPost("conversations/{id:guid}/delete")]
    [EnableRateLimiting("bot-read")]
    public async Task<IActionResult> DeleteChat(Guid id, CancellationToken ct)
    {
        var l = await Resolve(ct);
        var owner = l == null ? null : HistoryOwner(l);
        if (l == null || owner == null) return NotFound();
        if (!await _db.Conversations.AnyAsync(c => c.Id == id && c.OwnerId == owner, ct)) return NotFound();
        await Erase(new List<Guid> { id }, ct);
        return NoContent();
    }

    /// <summary>The visitor clears all of their own chats on this link.</summary>
    [HttpDelete("conversations")]
    [HttpPost("conversations/clear")]
    [EnableRateLimiting("bot-read")]
    public async Task<IActionResult> ClearChats(CancellationToken ct)
    {
        var l = await Resolve(ct);
        var owner = l == null ? null : HistoryOwner(l);
        if (l == null || owner == null) return NotFound();
        var ids = await _db.Conversations.Where(c => c.OwnerId == owner).Select(c => c.Id).ToListAsync(ct);
        if (ids.Count > 0) await Erase(ids, ct);
        return NoContent();
    }

    private async Task Erase(List<Guid> conversationIds, CancellationToken ct)
    {
        _db.ConversationMessages.RemoveRange(await _db.ConversationMessages.Where(m => conversationIds.Contains(m.ConversationId)).ToListAsync(ct));
        _db.Conversations.RemoveRange(await _db.Conversations.Where(c => conversationIds.Contains(c.Id)).ToListAsync(ct));
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Finds the link from its secret and, only if it and its company are active, scopes this request to that company.</summary>
    private async Task<BotLink?> Resolve(CancellationToken ct)
    {
        var token = Request.Headers["X-Bot-Token"].ToString();
        if (token.Length is < 16 or > 200) return null;
        var hash = ApiKeys.Hash(token);
        var l = await _db.BotLinks.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.TokenHash == hash && x.IsActive, ct);
        if (l == null) return null;
        if (!await _db.Tenants.AnyAsync(t => t.Id == l.TenantId && t.IsActive, ct)) return null;
        HttpContext.Items[HttpTenantContext.BotTenantItem] = l.TenantId;
        HttpContext.Items[HttpTenantContext.BotUserItem] = BotText.Owner(l.Id);
        if (l.LastUsedUtc == null || (DateTime.UtcNow - l.LastUsedUtc.Value).TotalMinutes > 5)
        {
            l.LastUsedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return l;
    }
}