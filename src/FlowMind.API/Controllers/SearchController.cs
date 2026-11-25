using System.ComponentModel.DataAnnotations;
using FlowMind.API.Authentication;
using FlowMind.Application.Interfaces;
using FlowMind.Infrastructure.Database;
using FlowMind.Infrastructure.Retrieval;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.API.Controllers;

public sealed class SearchRequest
{
    [Required] public Guid ApplicationId { get; set; }
    [Required, StringLength(RetrievalService.MaxQueryChars, MinimumLength = 2)] public string Query { get; set; } = "";
    [Range(1, 50)] public int? TopK { get; set; }
}

public sealed record SearchHitDto(Guid ChunkId, string Document, int Page, string Text, double Score, double Similarity);

/// <summary>
/// Retrieval over APPROVED documents of the caller's tenant. The tenant always comes from the token; the
/// application id is verified to belong to that tenant before anything is searched.
/// </summary>
[ApiController]
[Route("api/v1/search")]
[Authorize(Policy = "TenantMember")]
public class SearchController : ControllerBase
{
    private readonly FlowMindDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly RetrievalService _retrieval;
    private readonly ILogger<SearchController> _log;
    private readonly FlowMind.Infrastructure.Access.AccessResolver _access;

    public SearchController(FlowMindDbContext db, ITenantContext tenant, RetrievalService retrieval, ILogger<SearchController> log, FlowMind.Infrastructure.Access.AccessResolver access)
    {
        _db = db; _tenant = tenant; _retrieval = retrieval; _log = log; _access = access;
    }

    [HttpPost]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("ai")]
    public async Task<ActionResult<IReadOnlyList<SearchHitDto>>> Search(SearchRequest req, CancellationToken ct)
    {
        var tenantId = _tenant.TenantId!.Value;
        if (!await _db.Applications.AnyAsync(a => a.Id == req.ApplicationId, ct)) return NotFound();

        try
        {
            var scope = await User.ResolveScopeAsync(_access, tenantId, ct);
            var hits = await _retrieval.SearchAsync(tenantId, req.ApplicationId, req.Query, req.TopK, ct, null, scope.Unrestricted ? null : scope.Pages);
            return Ok(hits.Select(h => new SearchHitDto(h.ChunkId, h.DocumentName, h.PageNumber, h.Content, h.Score, h.Similarity)).ToList());
        }
        catch (QuotaExceededException ex)
        {
            return Problem(ex.Message, statusCode: 429);
        }
        catch (ProviderNotConfiguredException ex)
        {
            _log.LogWarning(ex, "Embedding provider not configured");
            return Problem("Search is unavailable: the embedding provider is not configured.", statusCode: 503);
        }
        catch (TransientProviderException)
        {
            return Problem("The embedding provider is temporarily unavailable. Try again shortly.", statusCode: 503);
        }
    }
}
