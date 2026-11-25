using System.ComponentModel.DataAnnotations;
using FlowMind.API.Authentication;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Infrastructure.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.API.Controllers;

public sealed class CreateAccessKeyRequest
{
    [Required, StringLength(200, MinimumLength = 2)] public string Name { get; set; } = "";
    /// <summary>Who this key is for. Omit for full access to all approved knowledge.</summary>
    public Guid? AudienceId { get; set; }
}
public sealed class UpdateAccessKeyRequest
{
    /// <summary>The audience to move the key to. Ignored when <see cref="FullAccess"/> is true.</summary>
    public Guid? AudienceId { get; set; }
    /// <summary>True gives the key full access (no audience).</summary>
    public bool? FullAccess { get; set; }
}
/// <summary>Key is returned only once, at creation.</summary>
public sealed record CreateAccessKeyResponse(Guid Id, string Name, string Key, Guid? AudienceId = null, string? AudienceName = null);
/// <param name="CanCopy">The key was stored (encrypted) and can be copied again. False for keys made before that existed.</param>
public sealed record AccessKeySummary(Guid Id, string Name, string Prefix, bool Active, DateTime CreatedUtc, DateTime? LastUsedUtc, Guid? AudienceId = null, string? AudienceName = null, bool CanCopy = false);

/// <summary>Company admins issue and revoke user keys (chat + read-only knowledge). Admin-only; tenant-scoped by query filter.</summary>
[ApiController]
[Route("api/v1/access-keys")]
[Authorize(Policy = "TenantAdmin")]
public class AccessKeysController : ControllerBase
{
    private const int MaxActiveKeys = 200;
    private readonly FlowMindDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IAuditLogger _audit;
    private readonly FlowMind.Infrastructure.Security.SecretBox _box;
    public AccessKeysController(FlowMindDbContext db, ITenantContext tenant, IAuditLogger audit, FlowMind.Infrastructure.Security.SecretBox box)
    { _db = db; _tenant = tenant; _audit = audit; _box = box; }

    [HttpGet]
    public async Task<IReadOnlyList<AccessKeySummary>> List(CancellationToken ct)
    {
        var audiences = await _db.Audiences.ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var keys = await _db.AccessKeys.OrderByDescending(k => k.CreatedUtc).ToListAsync(ct);
        return keys.Select(k => new AccessKeySummary(k.Id, k.Name, k.KeyPrefix, k.IsActive, k.CreatedUtc, k.LastUsedUtc,
            k.AudienceId, k.AudienceId is { } a ? audiences.GetValueOrDefault(a, "(removed)") : null, k.IsActive && k.KeyCipher != null)).ToList();
    }

    /// <summary>
    /// Replace a lost or leaked key in one step: a new key with the same name and audience is created and the old one is revoked
    /// (it stops working within about 15 seconds). The new key is returned once here and can be copied again later.
    /// </summary>
    [HttpPost("{id:guid}/replace")]
    public async Task<ActionResult<CreateAccessKeyResponse>> Replace(Guid id, CancellationToken ct)
    {
        var old = await _db.AccessKeys.FirstOrDefaultAsync(k => k.Id == id && k.IsActive, ct);
        if (old == null) return NotFound();
        var key = ApiKeys.Generate();
        var fresh = new TenantAccessKey { TenantId = old.TenantId, Name = old.Name, KeyPrefix = key[..8], KeyHash = ApiKeys.Hash(key), AudienceId = old.AudienceId,
            KeyCipher = _box.Enabled ? _box.Encrypt(key) : null };
        _db.AccessKeys.Add(fresh);
        old.IsActive = false; old.KeyCipher = null;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("accesskey.replace", "AccessKey", fresh.Id.ToString(), $"{old.Name}: replaced key {old.KeyPrefix}…", ct: ct);
        var audience = fresh.AudienceId is { } a ? await _db.Audiences.Where(x => x.Id == a).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        return Ok(new CreateAccessKeyResponse(fresh.Id, fresh.Name, key, fresh.AudienceId, audience));
    }

    /// <summary>
    /// Show a key again so the admin can copy it. Only for active keys that were stored encrypted; the key goes only to the signed-in company admin
    /// of this company (tenant-filtered), and every reveal is written to the audit log.
    /// </summary>
    [HttpGet("{id:guid}/key")]
    public async Task<IActionResult> Reveal(Guid id, CancellationToken ct)
    {
        var k = await _db.AccessKeys.FirstOrDefaultAsync(x => x.Id == id && x.IsActive, ct);
        var key = k == null ? null : _box.TryDecrypt(k.KeyCipher);
        if (k == null || key == null)
            return Problem("This key cannot be shown again (it was created before copying was possible, or it was revoked). Create a new key instead.", statusCode: 404);
        await _audit.LogAsync("accesskey.copy", "AccessKey", id.ToString(), k.Name, ct: ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(new { key });
    }

    [HttpPost]
    public async Task<ActionResult<CreateAccessKeyResponse>> Create(CreateAccessKeyRequest req, CancellationToken ct)
    {
        if (await _db.AccessKeys.CountAsync(k => k.IsActive, ct) >= MaxActiveKeys)
            return Problem($"Limit of {MaxActiveKeys} active keys reached. Revoke unused keys first.", statusCode: 409);
        string? audienceName = null;
        if (req.AudienceId is { } wanted)
        {
            audienceName = await _db.Audiences.Where(a => a.Id == wanted).Select(a => a.Name).FirstOrDefaultAsync(ct);   // tenant-filtered: another company's audience is "not found"
            if (audienceName == null) return Problem("That audience does not exist.", statusCode: 400);
        }

        var key = ApiKeys.Generate();
        var entity = new TenantAccessKey { TenantId = _tenant.TenantId!.Value, Name = req.Name.Trim(), KeyPrefix = key[..8], KeyHash = ApiKeys.Hash(key), AudienceId = req.AudienceId,
            KeyCipher = _box.Enabled ? _box.Encrypt(key) : null };
        _db.AccessKeys.Add(entity);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("accesskey.create", "AccessKey", entity.Id.ToString(), audienceName == null ? entity.Name : $"{entity.Name} ({audienceName})", ct: ct);
        return Created($"/api/v1/access-keys/{entity.Id}", new CreateAccessKeyResponse(entity.Id, entity.Name, key, entity.AudienceId, audienceName));
    }

    /// <summary>Move a key to another audience, or give it full access. Takes effect within about 15 seconds on every server.</summary>
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateAccessKeyRequest req, CancellationToken ct)
    {
        var key = await _db.AccessKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key == null) return NotFound();
        string? name = null;
        if (req.FullAccess == true) key.AudienceId = null;
        else if (req.AudienceId is { } wanted)
        {
            name = await _db.Audiences.Where(a => a.Id == wanted).Select(a => a.Name).FirstOrDefaultAsync(ct);
            if (name == null) return Problem("That audience does not exist.", statusCode: 400);
            key.AudienceId = wanted;
        }
        else return Problem("Choose an audience, or full access.", statusCode: 400);
        await _db.SaveChangesAsync(ct);
        FlowMind.Infrastructure.Access.AccessResolver.Invalidate(_tenant.TenantId!.Value);
        await _audit.LogAsync("accesskey.audience", "AccessKey", id.ToString(), $"{key.Name} → {(name ?? "full access")}", ct: ct);
        return NoContent();
    }

    /// <summary>Revoke a key. New sign-ins stop at once and any token it already issued is rejected within ~15 seconds.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var key = await _db.AccessKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key == null) return NotFound();
        key.IsActive = false;
        key.KeyCipher = null;   // a revoked key is never needed again
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("accesskey.revoke", "AccessKey", id.ToString(), key.Name, ct: ct);
        return NoContent();
    }
}
