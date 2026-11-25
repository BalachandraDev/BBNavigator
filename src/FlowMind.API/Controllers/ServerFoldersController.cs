using FlowMind.Domain.Entities;
using FlowMind.Infrastructure;
using FlowMind.Infrastructure.Database;
using FlowMind.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlowMind.API.Controllers;

public sealed class ServerFolderRequest { public string? Path { get; set; } }
/// <param name="FromConfig">Set in the server configuration, so it cannot be removed here.</param>
public sealed record ServerFolderRow(Guid? Id, string Path, bool FromConfig, bool Exists, int Companies);

/// <summary>
/// Platform administrator only: approve the locations on this server (a drive folder on the IIS machine, or a file share) where companies may choose to keep
/// their uploaded files. A company admin then types a folder inside one of them in Settings > Storage. A company can never add to this list.
/// </summary>
[ApiController]
[Route("api/v1/server-folders")]
[Authorize(Policy = "PlatformAdmin")]
public sealed class ServerFoldersController : ControllerBase
{
    private readonly FlowMindDbContext _db;
    private readonly FolderRoots _roots;
    private readonly string[] _configured;
    private readonly ILogger<ServerFoldersController> _log;

    public ServerFoldersController(FlowMindDbContext db, FolderRoots roots, IOptions<StorageOptions> options, ILogger<ServerFoldersController> log)
    { _db = db; _roots = roots; _configured = options.Value.AllowedFolderRoots ?? Array.Empty<string>(); _log = log; }

    [HttpGet]
    public async Task<IReadOnlyList<ServerFolderRow>> List(CancellationToken ct)
    {
        var approved = await _db.AllowedFolders.AsNoTracking().OrderBy(f => f.Path).ToListAsync(ct);
        var used = await _db.StorageSettings.IgnoreQueryFilters().Where(s => s.FolderPath != "").Select(s => s.FolderPath).ToListAsync(ct);
        int Count(string root) => used.Count(u => FolderGuard.Inside(u, root));
        var rows = _configured.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => new ServerFolderRow(null, c, true, SafeExists(c), Count(c)))
            .Concat(approved.Select(a => new ServerFolderRow(a.Id, a.Path, false, SafeExists(a.Path), Count(a.Path))));
        return rows.ToList();
    }

    [HttpPost]
    public async Task<IActionResult> Add(ServerFolderRequest r, CancellationToken ct)
    {
        var full = FolderRoots.CheckLocation(r.Path, out var why);
        if (full == null) return Problem(why, statusCode: 400);
        if ((await _roots.AllAsync(ct)).Any(x => string.Equals(x.TrimEnd('\\', '/'), full.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            return Problem("That location is already approved.", statusCode: 409);
        // the account FlowMind runs as must be able to use it, so a wrong permission is found now rather than at the first upload
        var probe = await new FolderObjectStore(full).ProbeAsync("", ct);
        if (!probe.Ok) return Problem("Not saved: " + probe.Message, statusCode: 400);
        var row = new AllowedFolder { Path = full };
        _db.AllowedFolders.Add(row);
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Platform admin approved server folder {Path}", full);
        return Ok(new ServerFolderRow(row.Id, full, false, true, 0));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var row = await _db.AllowedFolders.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (row == null) return NotFound();
        var inUse = await _db.StorageSettings.IgnoreQueryFilters().Where(s => s.FolderPath != "").Select(s => s.FolderPath).ToListAsync(ct);
        var n = inUse.Count(u => FolderGuard.Inside(u, row.Path));
        if (n > 0) return Problem($"{n} company folder setting(s) still use this location. Ask those companies to choose another location (and copy their files) first, because their files would stop opening.", statusCode: 409);
        _db.AllowedFolders.Remove(row);
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Platform admin removed approved server folder {Path}", row.Path);
        return NoContent();
    }

    private static bool SafeExists(string path) { try { return Directory.Exists(path); } catch { return false; } }
}
