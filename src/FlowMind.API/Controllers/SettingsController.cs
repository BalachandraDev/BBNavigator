using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Infrastructure;
using FlowMind.Infrastructure.Database;
using FlowMind.Infrastructure.Security;
using FlowMind.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlowMind.API.Controllers;

public sealed class StorageSettingsRequest
{
    [Required, RegularExpression("^(local|r2|s3|folder)$")] public string Provider { get; set; } = "local";
    /// <summary>"Folder on a server" only: the full path of a folder inside a location the server operator allowed (Storage:AllowedFolderRoots).</summary>
    [StringLength(400)] public string? FolderPath { get; set; }
    /// <summary>Cloudflare R2 only: the 32-character account id. The address is built from it.</summary>
    [RegularExpression("^([0-9a-fA-F]{32})?$", ErrorMessage = "The Cloudflare account id is 32 letters and digits.")] public string? AccountId { get; set; }
    /// <summary>Other S3-compatible services: the full https address.</summary>
    [StringLength(300)] public string? Endpoint { get; set; }
    [StringLength(63)] public string? Bucket { get; set; }
    [StringLength(40)] public string? Region { get; set; }
    [StringLength(100)] public string? Prefix { get; set; }
    [StringLength(128)] public string? AccessKeyId { get; set; }
    [StringLength(256)] public string? SecretAccessKey { get; set; }
}
public sealed class MigrateRequest
{
    [Required, RegularExpression("^(local|remote|folder)$")] public string To { get; set; } = "remote";
    /// <summary>Delete each file from the old location once the copy is verified.</summary>
    public bool RemoveSource { get; set; }
    [Range(1, 50)] public int Take { get; set; } = 20;
}
public sealed record StorageUsage(int Files, long Bytes);
public sealed record StorageTestInfo(DateTime? AtUtc, bool? Ok, string? Message);
public sealed record StorageSettingsView(string Provider, string RemoteType, string AccountId, string Endpoint, string Bucket, string Region, string Prefix,
    bool HasKeys, string? AccessKeyHint, StorageTestInfo LastTest, bool SecretsEnabled, bool AllowPrivateEndpoints, StorageUsage Local, StorageUsage Remote,
    string FolderPath, bool FolderEnabled, IReadOnlyList<string> FolderRoots, StorageUsage Folder);
public sealed record StorageTestResponse(bool Ok, string Message, long Milliseconds);
public sealed record MigrateFailure(string File, string Reason);
public sealed record MigrateResponse(int Moved, IReadOnlyList<MigrateFailure> Failed, int Remaining);
public sealed record DataLocation(string What, string Where, string Detail);
public sealed record DataOverview(IReadOnlyList<DataLocation> Locations, int Documents, int Pages, int KnowledgeItems, int Chats, int Messages, int SupportRequests);
public sealed record ActivityItem(DateTime TimeUtc, string Actor, string Action, string? Detail);
public sealed record SecurityCheck(string Name, bool? Ok, string Detail);

/// <summary>Company settings: where files are stored, where all data lives, the activity trail and a security checklist. Company admins only.</summary>
[ApiController]
[Route("api/v1/settings")]
[Authorize(Policy = "TenantAdmin")]
public class SettingsController : ControllerBase
{
    private static readonly Regex BucketRx = new("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.Compiled);
    private static readonly Regex RegionRx = new("^[a-z0-9-]{1,40}$", RegexOptions.Compiled);
    private static readonly Regex PrefixRx = new(@"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$", RegexOptions.Compiled);
    private static readonly Regex KeyIdRx = new(@"^[A-Za-z0-9/+=_.-]{3,128}$", RegexOptions.Compiled);

    private readonly FlowMindDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IFileStorage _files;
    private readonly IObjectStoreFactory _factory;
    private readonly SecretBox _box;
    private readonly IAuditLogger _audit;
    private readonly IConfiguration _config;
    private readonly bool _allowPrivate;
    private readonly FolderRoots _roots;

    public SettingsController(FlowMindDbContext db, ITenantContext tenant, IFileStorage files, IObjectStoreFactory factory, SecretBox box, IAuditLogger audit,
        IConfiguration config, IOptions<StorageOptions> storage, FolderRoots roots)
    { _db = db; _tenant = tenant; _files = files; _factory = factory; _box = box; _audit = audit; _config = config; _allowPrivate = storage.Value.AllowPrivateEndpoints; _roots = roots; }

    private Guid TenantId => _tenant.TenantId!.Value;

    // ---- storage ----------------------------------------------------------------------------------------------------

    [HttpGet("storage")]
    public async Task<StorageSettingsView> GetStorage(CancellationToken ct) => await ViewAsync(await _db.StorageSettings.FirstOrDefaultAsync(ct), ct);

    private async Task<StorageSettingsView> ViewAsync(StorageSetting? s, CancellationToken ct)
    {
        var rows = await _db.DocumentVersions.Where(v => _db.Documents.Any(d => d.Id == v.DocumentId))
            .GroupBy(v => v.StorageKind).Select(g => new { Kind = g.Key, Files = g.Count(), Bytes = g.Sum(v => v.SizeBytes) }).ToListAsync(ct);
        StorageUsage U(string kind) => rows.FirstOrDefault(r => r.Kind == kind) is { } r ? new StorageUsage(r.Files, r.Bytes) : new StorageUsage(0, 0);
        var folderRoots = await _roots.AllAsync(ct);
        var account = s?.RemoteType == "r2" && Regex.Match(s.Endpoint, "^https://([0-9a-f]{32})\\.r2\\.cloudflarestorage\\.com$") is { Success: true } m ? m.Groups[1].Value : "";
        return new StorageSettingsView(s?.Provider ?? "local", s?.RemoteType ?? "", account, s?.Endpoint ?? "", s?.Bucket ?? "", s?.Region ?? "", s?.Prefix ?? "",
            s?.AccessKeyCipher != null && s.SecretKeyCipher != null, s?.AccessKeyHint, new StorageTestInfo(s?.LastTestUtc, s?.LastTestOk, s?.LastTestMessage),
            _box.Enabled, _allowPrivate, U(RoutedFileStorage.Local), U(RoutedFileStorage.Remote),
            s?.FolderPath ?? "", folderRoots.Count > 0, folderRoots, U(RoutedFileStorage.Folder));
    }

    /// <summary>Checks the request and builds the connection to test or save. Blank keys keep the ones already saved.</summary>
    private (StorageConnection? Connection, string? RemoteType, string? Error) Build(StorageSettingsRequest r, StorageSetting? saved)
    {
        var type = r.Provider;
        string endpoint;
        if (type == "r2")
        {
            if (string.IsNullOrWhiteSpace(r.AccountId)) return (null, null, "Enter your Cloudflare account id.");
            endpoint = $"https://{r.AccountId.Trim().ToLowerInvariant()}.r2.cloudflarestorage.com";
        }
        else endpoint = (r.Endpoint ?? "").Trim().TrimEnd('/');
        if (endpoint.Length == 0) return (null, null, "Enter the storage address.");
        if (EndpointGuard.Check(endpoint, _allowPrivate) is { } bad) return (null, null, bad);

        var bucket = (r.Bucket ?? "").Trim();
        if (!BucketRx.IsMatch(bucket)) return (null, null, "The bucket name must be 3 to 63 lower-case letters, digits, dots or hyphens.");
        var region = string.IsNullOrWhiteSpace(r.Region) ? (type == "r2" ? "auto" : "us-east-1") : r.Region.Trim().ToLowerInvariant();
        if (!RegionRx.IsMatch(region)) return (null, null, "The region may only contain lower-case letters, digits and hyphens.");
        var prefix = (r.Prefix ?? "").Trim().Trim('/');
        if (prefix.Length > 0 && (!PrefixRx.IsMatch(prefix) || prefix.Contains(".."))) return (null, null, "The folder may only contain letters, digits, dots, hyphens, underscores and slashes.");

        var key = string.IsNullOrWhiteSpace(r.AccessKeyId) ? _box.TryDecrypt(saved?.AccessKeyCipher) : r.AccessKeyId.Trim();
        var secret = string.IsNullOrWhiteSpace(r.SecretAccessKey) ? _box.TryDecrypt(saved?.SecretKeyCipher) : r.SecretAccessKey.Trim();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret)) return (null, null, "Enter the access key id and the secret access key.");
        if (!KeyIdRx.IsMatch(key)) return (null, null, "The access key id has characters that are not allowed.");
        return (new StorageConnection(endpoint, bucket, region, prefix, key, secret), type, null);
    }

    private async Task<StorageProbeResult> ProbeAsync(StorageConnection c, CancellationToken ct)
    {
        try { return await _factory.Create(c).ProbeAsync(RoutedFileStorage.Key(c.Prefix, ""), ct); }
        catch (StorageUnavailableException ex) { return new StorageProbeResult(false, ex.Message, 0); }
    }

    [HttpPost("storage/test")]
    public async Task<ActionResult<StorageTestResponse>> TestStorage(StorageSettingsRequest r, CancellationToken ct)
    {
        if (r.Provider == "local") return new StorageTestResponse(true, "Files are stored in this server's private storage folder.", 0);
        if (r.Provider == "folder")
        {
            var folder = FolderGuard.Check(r.FolderPath, await _roots.AllAsync(ct), out var why);
            if (folder == null) return Problem(why, statusCode: 400);
            var fr = await new FolderObjectStore(folder).ProbeAsync("", ct);
            var cur = await _db.StorageSettings.FirstOrDefaultAsync(ct);
            if (cur != null) { cur.LastTestUtc = DateTime.UtcNow; cur.LastTestOk = fr.Ok; cur.LastTestMessage = fr.Message; await _db.SaveChangesAsync(ct); }
            return new StorageTestResponse(fr.Ok, fr.Message, fr.Milliseconds);
        }
        var saved = await _db.StorageSettings.FirstOrDefaultAsync(ct);
        var (c, _, error) = Build(r, saved);
        if (c == null) return Problem(error, statusCode: 400);
        var res = await ProbeAsync(c, ct);
        if (saved != null) { saved.LastTestUtc = DateTime.UtcNow; saved.LastTestOk = res.Ok; saved.LastTestMessage = res.Message; await _db.SaveChangesAsync(ct); }
        return new StorageTestResponse(res.Ok, res.Message, res.Milliseconds);
    }

    [HttpPut("storage")]
    public async Task<ActionResult<StorageSettingsView>> SaveStorage(StorageSettingsRequest r, CancellationToken ct)
    {
        var s = await _db.StorageSettings.FirstOrDefaultAsync(ct);
        if (r.Provider == "local")
        {
            if (s != null) { s.Provider = "local"; s.UpdatedUtc = DateTime.UtcNow; await _db.SaveChangesAsync(ct); }
            await _audit.LogAsync("settings.storage", "Storage", null, "New files go to this server", ct: ct);
            return await ViewAsync(s, ct);
        }
        if (r.Provider == "folder")
        {
            var folder = FolderGuard.Check(r.FolderPath, await _roots.AllAsync(ct), out var why);
            if (folder == null) return Problem(why, statusCode: 400);
            var fr = await new FolderObjectStore(folder).ProbeAsync("", ct);   // only accepted once a test file has been written, read and removed
            if (!fr.Ok)
            {
                if (s != null) { s.LastTestUtc = DateTime.UtcNow; s.LastTestOk = false; s.LastTestMessage = fr.Message; await _db.SaveChangesAsync(ct); }
                return Problem("Not saved: " + fr.Message, statusCode: 400);
            }
            if (s == null) { s = new StorageSetting { TenantId = TenantId }; _db.StorageSettings.Add(s); }
            s.Provider = "folder"; s.FolderPath = folder;
            s.UpdatedUtc = DateTime.UtcNow; s.LastTestUtc = DateTime.UtcNow; s.LastTestOk = true; s.LastTestMessage = fr.Message;
            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync("settings.storage", "Storage", null, $"New files go to the server folder {folder}", ct: ct);
            return await ViewAsync(s, ct);
        }
        if (!_box.Enabled) return Problem("Cloud storage needs a server secret (Security:SecretsKey) so the access keys can be stored encrypted.", statusCode: 409);

        var (c, type, error) = Build(r, s);
        if (c == null) return Problem(error, statusCode: 400);
        var probe = await ProbeAsync(c, ct);   // a location is only accepted once it has been proven to work
        if (!probe.Ok)
        {
            if (s != null) { s.LastTestUtc = DateTime.UtcNow; s.LastTestOk = false; s.LastTestMessage = probe.Message; await _db.SaveChangesAsync(ct); }
            return Problem("Not saved: " + probe.Message, statusCode: 400);
        }

        if (s == null) { s = new StorageSetting { TenantId = TenantId }; _db.StorageSettings.Add(s); }
        s.Provider = r.Provider; s.RemoteType = type!; s.Endpoint = c.Endpoint; s.Bucket = c.Bucket; s.Region = c.Region; s.Prefix = c.Prefix;
        s.AccessKeyCipher = _box.Encrypt(c.AccessKey); s.SecretKeyCipher = _box.Encrypt(c.SecretKey);
        s.AccessKeyHint = c.AccessKey.Length <= 4 ? c.AccessKey : c.AccessKey[^4..];
        s.UpdatedUtc = DateTime.UtcNow; s.LastTestUtc = DateTime.UtcNow; s.LastTestOk = true; s.LastTestMessage = probe.Message;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("settings.storage", "Storage", null, $"New files go to {(type == "r2" ? "Cloudflare R2" : "S3-compatible storage")}, bucket {c.Bucket}", ct: ct);
        return await ViewAsync(s, ct);
    }

    /// <summary>Copies existing files to the other location in small batches (the page repeats this until none are left). Each copy is checked against the file's SHA-256 before it is switched over.</summary>
    [HttpPost("storage/migrate")]
    public async Task<ActionResult<MigrateResponse>> Migrate(MigrateRequest r, CancellationToken ct)
    {
        if (r.To == RoutedFileStorage.Remote)
        {
            var s = await _db.StorageSettings.FirstOrDefaultAsync(ct);
            if (RoutedFileStorage.Connection(s, _box) == null) return Problem("Set up and save cloud storage first.", statusCode: 409);
        }
        if (r.To == RoutedFileStorage.Folder)
        {
            var s = await _db.StorageSettings.FirstOrDefaultAsync(ct);
            if (RoutedFileStorage.FolderRoot(s, await _roots.AllAsync(ct)) == null) return Problem("Set up and save the server folder first.", statusCode: 409);
        }
        var todo = _db.DocumentVersions.Where(v => v.StorageKind != r.To && _db.Documents.Any(d => d.Id == v.DocumentId));
        var batch = await todo.OrderBy(v => v.CreatedUtc).Take(r.Take).ToListAsync(ct);
        var failed = new List<MigrateFailure>(); var moved = 0;
        foreach (var v in batch)
        {
            var name = await _db.Documents.Where(d => d.Id == v.DocumentId).Select(d => d.FileName).FirstOrDefaultAsync(ct) ?? "file";
            var from = v.StorageKind;
            try
            {
                var stored = await _files.CopyAsync(v.StoragePath, from, r.To, TenantId, v.ContentHash, ct);
                v.StorageKind = stored.Kind;
                await _db.SaveChangesAsync(ct);          // from here on every read uses the new location
                if (r.RemoveSource) { try { await _files.RemoveAsync(v.StoragePath, from, TenantId, ct); } catch { /* the copy is safe; the old file is only left behind */ } }
                moved++;
            }
            catch (Exception ex) when (ex is StorageUnavailableException or InvalidDataException or IOException or InvalidOperationException)
            { failed.Add(new MigrateFailure($"{name} v{v.VersionNumber}", ex.Message)); }
        }
        if (moved > 0) await _audit.LogAsync("settings.storage-copy", "Storage", null, $"{moved} file(s) copied to {r.To}{(r.RemoveSource ? " and removed from the old location" : "")}", ct: ct);
        var failedIds = failed.Count;
        var remaining = await todo.CountAsync(ct);
        return new MigrateResponse(moved, failed, remaining);
    }

    // ---- where everything lives ----------------------------------------------------------------------------------------

    [HttpGet("overview")]
    public async Task<DataOverview> Overview(CancellationToken ct)
    {
        var storage = await ViewAsync(await _db.StorageSettings.FirstOrDefaultAsync(ct), ct);
        var docs = await _db.Documents.CountAsync(ct);
        var pages = await _db.DocumentPages.CountAsync(ct);
        var items = await _db.AppPages.CountAsync(ct) + await _db.Workflows.CountAsync(ct) + await _db.BusinessRules.CountAsync(ct) + await _db.ValidationRules.CountAsync(ct) + await _db.PageFunctionalities.CountAsync(ct);
        var chats = await _db.Conversations.CountAsync(ct);
        var messages = await _db.ConversationMessages.CountAsync(ct);
        var support = await _db.SupportRequests.CountAsync(ct);
        string Mb(long b) => b >= 1_048_576 ? $"{b / 1_048_576.0:0.#} MB" : $"{Math.Max(0, b) / 1024.0:0.#} KB";
        var cloud = storage.Provider == "r2" ? "Cloudflare R2" : storage.Provider == "s3" ? "Your S3-compatible cloud" : null;
        var files = $"{storage.Local.Files} on this server ({Mb(storage.Local.Bytes)})" + (storage.Remote.Files > 0 || cloud != null ? $", {storage.Remote.Files} in the cloud ({Mb(storage.Remote.Bytes)})" : "")
            + (storage.Folder.Files > 0 || storage.Provider == "folder" ? $", {storage.Folder.Files} in your server folder ({Mb(storage.Folder.Bytes)})" : "");
        return new DataOverview(new DataLocation[]
        {
            new("Original files (PDF, DOCX, TXT)", cloud != null ? $"New uploads: {cloud}" : storage.Provider == "folder" ? $"New uploads: server folder {storage.FolderPath}" : "New uploads: this server", files),
            new("Extracted text and reviewed knowledge", "Server database (SQL Server)", $"{pages} page(s), {items} knowledge item(s)"),
            new("Chats, support requests and insights", "Server database (SQL Server)", $"{chats} chat(s), {messages} message(s), {support} support request(s)"),
            new("Search index (embeddings)", "Server database (PostgreSQL with pgvector)", "Rebuilt from approved documents at any time"),
            new("Access keys and storage credentials", "Server database, encrypted with the server secret", "Never shown again after saving"),
        }, docs, pages, items, chats, messages, support);
    }

    [HttpGet("activity")]
    public async Task<IReadOnlyList<ActivityItem>> Activity([FromQuery] int take = 40, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 100);
        var id = TenantId;
        var rows = await _db.AuditLogs.Where(a => a.TenantId == id).OrderByDescending(a => a.Id).Take(take).ToListAsync(ct);
        return rows.Select(a => new ActivityItem(DateTime.SpecifyKind(a.TimestampUtc, DateTimeKind.Utc), a.Actor, a.Action, a.Detail)).ToList();
    }

    [HttpGet("security")]
    public async Task<IReadOnlyList<SecurityCheck>> Security(CancellationToken ct)
    {
        var s = await _db.StorageSettings.FirstOrDefaultAsync(ct);
        var keys = await _db.AccessKeys.CountAsync(k => k.IsActive, ct);
        var links = await _db.BotLinks.CountAsync(l => l.IsActive, ct);
        return new SecurityCheck[]
        {
            new("Server secret for encryption", _box.Enabled, _box.Enabled ? "Set. Access keys and cloud credentials are stored encrypted." : "Not set. Cloud storage and recoverable keys are switched off until Security:SecretsKey is set."),
            new("HTTPS", _config.GetValue("Security:RequireHttps", true), _config.GetValue("Security:RequireHttps", true) ? "The server refuses plain http." : "The app itself does not enforce https. That is fine behind a reverse proxy that provides it; otherwise put one in front before real use."),
            new("Cloud storage addresses", !_allowPrivate, _allowPrivate ? "Private network addresses are allowed (set by the server operator)." : "Only public https addresses are accepted; private networks and this server are blocked."),
            new("Cloud storage credentials", s?.AccessKeyCipher == null ? null : true, s?.AccessKeyCipher == null ? "No cloud storage saved." : $"Saved encrypted (key ending {s.AccessKeyHint}); never shown again."),
            new("Server folder storage", true, (await _roots.AllAsync(ct)).Count > 0
                ? "On, limited to locations the platform administrator approved. A typed folder outside them is refused."
                : "Off. A company admin cannot point storage at a folder until the platform administrator approves a location (Companies > Server folders)."),
            new("Open access", null, $"{keys} active user key(s) and {links} active share link(s). Revoke any you no longer need."),
            new("AI providers", true, "Free tiers only. When a daily limit is reached the feature pauses; nothing paid is used."),
        };
    }
}