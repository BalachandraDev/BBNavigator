using System.ComponentModel.DataAnnotations;
using FlowMind.API.Authentication;
using FlowMind.Application.Interfaces;
using FlowMind.Infrastructure.Chat;
using FlowMind.Infrastructure.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.API.Controllers;

public sealed class AskRequest
{
    /// <summary>Your question. Long messages are fine: greetings, signatures, repetition and pasted bulk are removed locally before any AI call.</summary>
    [Required, StringLength(8000, MinimumLength = 2)] public string Message { get; set; } = "";
    /// <summary>Optional. Only needed if your company has more than one application: its name or id.</summary>
    public string? Application { get; set; }
    /// <summary>Optional. Pass the id returned earlier to ask a follow-up in the same conversation.</summary>
    public Guid? ConversationId { get; set; }
    /// <summary>"en" or "ta": the language of the reply. Omit it and the reply follows the language of the question.</summary>
    [RegularExpression("^(en|ta)?$")] public string? Language { get; set; }
}

public sealed record AskSource(string Document, int Page);
/// <param name="Understood">Only set when a long message was condensed: the question that was actually asked.</param>
/// <param name="Related">Only set when "documented" is false but relevant sections were found: the closest document sections.</param>
public sealed record AskResponse(string Answer, bool Documented, IReadOnlyList<AskSource> Sources, Guid ConversationId, string Application, string? Understood = null, IReadOnlyList<AskSource>? Related = null);

/// <summary>
/// The one call most integrations need: send a message, get an answer taken from the approved documents, with its sources.
/// Authenticate with the header <c>X-Api-Key: &lt;your key&gt;</c> (a user key from the Access page, or the company key).
/// </summary>
[ApiController]
[Route("api")]
[Authorize(Policy = "TenantMember", AuthenticationSchemes = ApiKeyAuthHandler.SchemeName + "," + Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)]
public class AskController : ControllerBase
{
    private readonly FlowMindDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ChatService _chat;
    private readonly ILogger<AskController> _log;
    private readonly FlowMind.Infrastructure.Access.AccessResolver _access;

    public AskController(FlowMindDbContext db, ITenantContext tenant, ChatService chat, ILogger<AskController> log, FlowMind.Infrastructure.Access.AccessResolver access)
    {
        _db = db; _tenant = tenant; _chat = chat; _log = log; _access = access;
    }

    /// <summary>Ask a question about your documentation.</summary>
    /// <response code="200">Answer plus the document and page it came from. <c>documented: false</c> means the documents do not cover it.</response>
    /// <response code="400">Several applications exist and none was named, or the named one was not found.</response>
    /// <response code="401">Missing or invalid API key.</response>
    /// <response code="429">A daily free-tier limit (or the per-minute limit) has been reached. Nothing paid is used instead.</response>
    [HttpPost("ask")]
    [EnableRateLimiting("ai")]
    [ProducesResponseType(typeof(AskResponse), 200)]
    public async Task<ActionResult<AskResponse>> Ask(AskRequest req, CancellationToken ct)
    {
        var apps = await _db.Applications.OrderBy(a => a.Name).Select(a => new { a.Id, a.Name }).ToListAsync(ct);
        if (apps.Count == 0) return Problem("No application has been set up for this company yet, so there are no documents to answer from.", statusCode: 409);

        var wanted = req.Application?.Trim();
        var app = string.IsNullOrEmpty(wanted)
            ? (apps.Count == 1 ? apps[0] : null)
            : apps.FirstOrDefault(a => (Guid.TryParse(wanted, out var id) && a.Id == id) || string.Equals(a.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (app == null)
            return Problem(string.IsNullOrEmpty(wanted)
                ? $"Your company has {apps.Count} applications. Add \"application\": \"<name>\" to the request. Available: {string.Join(", ", apps.Select(a => a.Name))}."
                : $"Application \"{wanted}\" was not found. Available: {string.Join(", ", apps.Select(a => a.Name))}.", statusCode: 400);

        try
        {
            var userId = _tenant.UserId ?? throw new InvalidOperationException("No signed-in caller.");
            var scope = await User.ResolveScopeAsync(_access, _tenant.TenantId!.Value, ct);   // the key's audience decides which pages can answer
            var a = await _chat.AskAsync(_tenant.TenantId!.Value, userId, app.Id, req.ConversationId, req.Message, ct, null, req.Language, scope);
            return new AskResponse(a.Answer, a.Grounded, a.Citations.Select(c => new AskSource(c.Document, c.Page)).ToList(), a.ConversationId, app.Name, a.Understood,
                a.Related?.Select(c => new AskSource(c.Document, c.Page)).ToList());
        }
        catch (ConversationNotFoundException) { return Problem("That conversationId was not found.", statusCode: 404); }
        catch (QuotaExceededException ex) { return Problem(ex.Message, statusCode: 429); }
        catch (ProviderNotConfiguredException ex)
        {
            _log.LogWarning(ex, "AI provider not configured for ask");
            return Problem("Answers are unavailable: an AI provider is not configured on the server.", statusCode: 503);
        }
        catch (Exception ex) when (ex is TransientProviderException or InvalidAnalysisOutputException)
        {
            _log.LogWarning(ex, "AI provider problem for ask");
            return Problem("The AI service could not produce an answer. Try again shortly.", statusCode: 503);
        }
    }
}
