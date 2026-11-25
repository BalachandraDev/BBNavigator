using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FlowMind.API.Authentication;
using FlowMind.Application.Interfaces;
using FlowMind.Infrastructure.Chat;
using FlowMind.Infrastructure.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.API.Controllers;

public sealed class ChatRequest
{
    [Required] public Guid ApplicationId { get; set; }
    /// <summary>Omit to start a new conversation; pass the returned id for follow-up questions.</summary>
    public Guid? ConversationId { get; set; }
    [Required, StringLength(8000, MinimumLength = 2)] public string Message { get; set; } = "";
    /// <summary>"en" or "ta": the language of the reply. Omit it and the reply follows the language of the question.</summary>
    [RegularExpression("^(en|ta)?$")] public string? Language { get; set; }
}

public sealed class ChatSupportRequest
{
    [Required] public Guid ConversationId { get; set; }
    [StringLength(500)] public string? Note { get; set; }
    [StringLength(120)] public string? Contact { get; set; }
}

/// <param name="Understood">Only set when a long message was condensed: the question that was actually asked.</param>
public sealed record ChatResponse(Guid ConversationId, string Answer, bool Documented, IReadOnlyList<CitationDto> Sources, string? Understood = null, IReadOnlyList<CitationDto>? Related = null);
public sealed record CitationDto(string Document, int Page);
public sealed record ConversationSummary(Guid Id, string Title, DateTime UpdatedUtc);
public sealed record ConversationMessageDto(string Role, string Content, bool? Documented, IReadOnlyList<CitationDto> Sources, DateTime CreatedUtc);
public sealed record ConversationDetail(Guid Id, Guid ApplicationId, string Title, IReadOnlyList<ConversationMessageDto> Messages);

/// <summary>Chat over approved documentation. Tenant comes from the token only; conversations are tenant-scoped by query filter.</summary>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = "TenantMember")]
public class ChatController : ControllerBase
{
    private readonly FlowMindDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ChatService _chat;
    private readonly ILogger<ChatController> _log;
    private readonly FlowMind.Infrastructure.Insights.SupportService _support;
    private readonly FlowMind.Infrastructure.Access.AccessResolver _access;

    public ChatController(FlowMindDbContext db, ITenantContext tenant, ChatService chat, ILogger<ChatController> log, FlowMind.Infrastructure.Insights.SupportService support,
        FlowMind.Infrastructure.Access.AccessResolver access)
    {
        _db = db; _tenant = tenant; _chat = chat; _log = log; _support = support; _access = access;
    }

    // Conversations belong to the user who started them: every lookup below filters on the token subject.
    private string UserId => _tenant.UserId ?? throw new InvalidOperationException("No signed-in user.");

    [HttpPost("chat")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("ai")]
    public async Task<ActionResult<ChatResponse>> Ask(ChatRequest req, CancellationToken ct)
    {
        if (!await _db.Applications.AnyAsync(a => a.Id == req.ApplicationId, ct)) return NotFound();
        try
        {
            var scope = await User.ResolveScopeAsync(_access, _tenant.TenantId!.Value, ct);   // what this key's audience may see
            var a = await _chat.AskAsync(_tenant.TenantId!.Value, UserId, req.ApplicationId, req.ConversationId, req.Message, ct, null, req.Language, scope);
            return new ChatResponse(a.ConversationId, a.Answer, a.Grounded, a.Citations.Select(c => new CitationDto(c.Document, c.Page)).ToList(), a.Understood,
                a.Related?.Select(c => new CitationDto(c.Document, c.Page)).ToList());
        }
        catch (ConversationNotFoundException) { return NotFound(); }
        catch (QuotaExceededException ex) { return Problem(ex.Message, statusCode: 429); }
        catch (ProviderNotConfiguredException ex)
        {
            _log.LogWarning(ex, "AI provider not configured for chat");
            return Problem("Chat is unavailable: an AI provider is not configured.", statusCode: 503);
        }
        catch (Exception ex) when (ex is TransientProviderException or InvalidAnalysisOutputException)
        {
            _log.LogWarning(ex, "Chat provider problem");
            return Problem("The AI service could not produce an answer. Try again shortly.", statusCode: 503);
        }
    }

    /// <summary>"Send this to support": ask the company's admins to follow up on the last question of one of your own chats.</summary>
    [HttpPost("chat/support")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("support")]
    public async Task<IActionResult> Support(ChatSupportRequest req, CancellationToken ct)
    {
        try
        {
            await _support.CreateAsync(_tenant.TenantId!.Value, "Signed-in users", UserId, req.ConversationId, req.Note, req.Contact, ct);
            return Ok(new { sent = true });
        }
        catch (FlowMind.Infrastructure.Insights.SupportRejectedException ex) { return Problem(ex.Message, statusCode: ex.StatusCode); }
    }

    [HttpGet("conversations")]
    public async Task<IReadOnlyList<ConversationSummary>> List([FromQuery, Required] Guid applicationId, CancellationToken ct) =>
        await _db.Conversations.Where(c => c.ApplicationId == applicationId && c.OwnerId == UserId)
            .OrderByDescending(c => c.UpdatedUtc).Take(100)
            .Select(c => new ConversationSummary(c.Id, c.Title, c.UpdatedUtc)).ToListAsync(ct);

    [HttpGet("conversations/{id:guid}")]
    public async Task<ActionResult<ConversationDetail>> Get(Guid id, CancellationToken ct)
    {
        var c = await _db.Conversations.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == UserId, ct);
        if (c == null) return NotFound();
        var msgs = await _db.ConversationMessages.Where(m => m.ConversationId == id).OrderBy(m => m.Id).ToListAsync(ct);
        return new ConversationDetail(c.Id, c.ApplicationId, c.Title, msgs.Select(m => new ConversationMessageDto(
            m.Role, m.Content, m.Role == "assistant" ? m.Grounded : null, ParseSources(m.CitationsJson), m.CreatedUtc)).ToList());
    }

    [HttpDelete("conversations/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var c = await _db.Conversations.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == UserId, ct);
        if (c == null) return NotFound();
        _db.Conversations.Remove(c); // messages cascade
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static IReadOnlyList<CitationDto> ParseSources(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<CitationDto>();
        try { return JsonSerializer.Deserialize<List<CitationDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
        catch (JsonException) { return Array.Empty<CitationDto>(); }
    }
}
