using System.Text.RegularExpressions;
using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Infrastructure.Database;
using FlowMind.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.Storage;

/// <summary>
/// The IFileStorage the rest of the app uses. For each company it looks up Settings > Storage: new files go to the active location
/// (this server, the company's own bucket, or a folder on a server such as the IIS machine), and every stored file remembers where it is
/// (DocumentVersion.StorageKind), so switching the active location never strands older files. Nothing silently falls back: if the chosen
/// location is unusable, the upload fails with a clear message.
/// </summary>
public sealed class RoutedFileStorage : IFileStorage
{
    public const string Local = "local", Remote = "remote", Folder = "folder";
    private static readonly Regex SafePath = new(@"^[0-9a-f]{32}/[0-9a-f]{32}/v\d{1,4}/original\.[a-z0-9]{1,5}$", RegexOptions.Compiled);

    private readonly LocalFileStorage _local;
    private readonly FlowMindDbContext _db;
    private readonly SecretBox _box;
    private readonly IObjectStoreFactory _factory;
    private readonly ILogger<RoutedFileStorage> _log;
    private readonly FolderRoots? _roots;

    public RoutedFileStorage(LocalFileStorage local, FlowMindDbContext db, SecretBox box, IObjectStoreFactory factory, ILogger<RoutedFileStorage> log, FolderRoots? roots = null)
    { _local = local; _db = db; _box = box; _factory = factory; _log = log; _roots = roots; }

    public Task<StagedFile> StageAsync(Stream content, long maxBytes, CancellationToken ct) => _local.StageAsync(content, maxBytes, ct);
    public void Discard(StagedFile staged) => _local.Discard(staged);

    // ---- settings -------------------------------------------------------------------------------------------------

    private Task<StorageSetting?> SettingAsync(Guid tenantId, CancellationToken ct) =>
        _db.StorageSettings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

    /// <summary>The connection a company saved (decrypted), or null when none is saved or the keys cannot be read.</summary>
    public static StorageConnection? Connection(StorageSetting? s, SecretBox box)
    {
        if (s == null || string.IsNullOrEmpty(s.Endpoint) || string.IsNullOrEmpty(s.Bucket)) return null;
        var key = box.TryDecrypt(s.AccessKeyCipher); var secret = box.TryDecrypt(s.SecretKeyCipher);
        return key == null || secret == null ? null : new StorageConnection(s.Endpoint, s.Bucket, s.Region, s.Prefix, key, secret);
    }

    /// <summary>
    /// The company's saved folder, checked again NOW against the operator's allowed roots (the operator may have changed them since it was saved).
    /// Null when no folder is saved or it is no longer allowed.
    /// </summary>
    public static string? FolderRoot(StorageSetting? s, IEnumerable<string> allowedRoots) =>
        s == null || string.IsNullOrWhiteSpace(s.FolderPath) ? null : FolderGuard.Check(s.FolderPath, allowedRoots, out _);

    private async Task<(IObjectStore Store, string Prefix)> RemoteAsync(Guid tenantId, CancellationToken ct)
    {
        var c = Connection(await SettingAsync(tenantId, ct), _box)
            ?? throw new StorageUnavailableException("Cloud storage is not set up or its keys can no longer be read. Check Settings > Storage.");
        return (_factory.Create(c), c.Prefix);
    }

    private async Task<(IObjectStore Store, string Prefix)> FolderAsync(Guid tenantId, CancellationToken ct)
    {
        var s = await SettingAsync(tenantId, ct);
        var root = FolderRoot(s, _roots == null ? Array.Empty<string>() : await _roots.AllAsync(ct))
            ?? throw new StorageUnavailableException(string.IsNullOrWhiteSpace(s?.FolderPath)
                ? "The server folder is not set up. Check Settings > Storage."
                : "The saved server folder is no longer allowed on this server. Check Settings > Storage, or ask the person who runs the server.");
        return (new FolderObjectStore(root), "");
    }

    /// <summary>The store and key prefix for a stored file's location kind ("remote" = cloud bucket, "folder" = server folder).</summary>
    private Task<(IObjectStore Store, string Prefix)> StoreAsync(string kind, Guid tenantId, CancellationToken ct) =>
        kind == Folder ? FolderAsync(tenantId, ct) : RemoteAsync(tenantId, ct);

    private static string Where(string kind) => kind == Folder ? "your server folder" : "your cloud storage";

    /// <summary>The full object key for a relative path inside a company's bucket folder.</summary>
    public static string Key(string prefix, string relative) => (string.IsNullOrEmpty(prefix) ? "" : prefix.Trim('/') + "/") + relative;

    private static string Safe(string path, Guid tenantId)
    {
        var p = (path ?? "").Replace('\\', '/');
        if (!SafePath.IsMatch(p) || !p.StartsWith(tenantId.ToString("N") + "/", StringComparison.Ordinal))
            throw new InvalidOperationException("That file path does not belong to this company.");   // tenant isolation holds even if a row were wrong
        return p;
    }

    // ---- writing --------------------------------------------------------------------------------------------------

    public async Task<StoredFile> CommitAsync(StagedFile staged, Guid tenantId, Guid documentId, int versionNumber, string extension, CancellationToken ct)
    {
        var s = await SettingAsync(tenantId, ct);
        var kind = s?.Provider is "r2" or "s3" ? Remote : s?.Provider == "folder" ? Folder : null;
        if (kind != null)
        {
            var ext = extension.TrimStart('.').ToLowerInvariant();
            if (ext.Length is 0 or > 5 || !ext.All(char.IsLetterOrDigit)) throw new ArgumentException("Invalid extension.", nameof(extension));
            var relative = $"{tenantId:N}/{documentId:N}/v{versionNumber}/original.{ext}";
            var (store, prefix) = await StoreAsync(kind, tenantId, ct);
            try { await store.PutFileAsync(Key(prefix, relative), staged.TempPath, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw new StorageUnavailableException($"The file could not be saved to {Where(kind)}. Check Settings > Storage.", ex); }
            _local.Discard(staged);
            return new StoredFile(relative, kind);
        }
        return new StoredFile(_local.Commit(staged, tenantId, documentId, versionNumber, extension), Local);
    }

    // ---- reading and moving -----------------------------------------------------------------------------------------

    public async Task<Stream> OpenReadAsync(string path, string kind, Guid tenantId, CancellationToken ct)
    {
        var p = Safe(path, tenantId);
        if (kind is not (Remote or Folder)) return _local.OpenRead(p);
        var (store, prefix) = await StoreAsync(kind, tenantId, ct);
        try { return await store.GetAsync(Key(prefix, p), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new StorageUnavailableException($"The file could not be read from {Where(kind)}. Check Settings > Storage.", ex); }
    }

    public async Task<StoredFile> CopyAsync(string path, string fromKind, string toKind, Guid tenantId, string expectedSha256, CancellationToken ct)
    {
        var p = Safe(path, tenantId);
        if (fromKind == toKind) throw new InvalidOperationException("The file is already there.");
        StagedFile staged;
        await using (var source = await OpenReadAsync(p, fromKind, tenantId, ct))
            staged = await _local.StageAsync(source, long.MaxValue, ct);
        try
        {
            if (!string.Equals(staged.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The copy did not match the original (checksum differs), so nothing was changed.");
            if (toKind is Remote or Folder)
            {
                var (store, prefix) = await StoreAsync(toKind, tenantId, ct);
                await store.PutFileAsync(Key(prefix, p), staged.TempPath, ct);
                _local.Discard(staged);
            }
            else _local.CommitAt(staged, p);
            return new StoredFile(p, toKind);
        }
        catch { _local.Discard(staged); throw; }
    }

    // ---- deleting -------------------------------------------------------------------------------------------------

    public async Task RemoveAsync(string path, string kind, Guid tenantId, CancellationToken ct)
    {
        var p = Safe(path, tenantId);
        if (kind is not (Remote or Folder)) { _local.DeleteFile(p); return; }
        var (store, prefix) = await StoreAsync(kind, tenantId, ct);
        await store.DeleteAsync(Key(prefix, p), ct);
    }

    public Task DeleteDocumentAsync(Guid tenantId, Guid documentId, CancellationToken ct) =>
        DeleteEverywhereAsync(tenantId, $"{tenantId:N}/{documentId:N}/", () => _local.DeleteDocument(tenantId, documentId), ct);

    public Task DeleteTenantAsync(Guid tenantId, CancellationToken ct) =>
        DeleteEverywhereAsync(tenantId, $"{tenantId:N}/", () => _local.DeleteTenant(tenantId), ct);

    public Task DeleteVersionAsync(Guid tenantId, Guid documentId, int versionNumber, CancellationToken ct) =>
        DeleteEverywhereAsync(tenantId, $"{tenantId:N}/{documentId:N}/v{versionNumber}/", () => _local.DeleteVersion(tenantId, documentId, versionNumber), ct);

    /// <summary>
    /// Removes from this server and, if the company has a bucket or a server folder saved, from there too. A failure in a remote location is reported
    /// after everything else is done. Only the company's own subfolders are ever removed, never the chosen folder itself.
    /// </summary>
    private async Task DeleteEverywhereAsync(Guid tenantId, string relativePrefix, Action local, CancellationToken ct)
    {
        local();
        var s = await SettingAsync(tenantId, ct);
        var problems = new List<string>();

        var c = Connection(s, _box);
        if (c != null)
        {
            try { await _factory.Create(c).DeletePrefixAsync(Key(c.Prefix, relativePrefix), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _log.LogError(ex, "Could not remove files under {Prefix} from the company's cloud storage", relativePrefix); problems.Add("your cloud storage"); }
        }
        if (!string.IsNullOrWhiteSpace(s?.FolderPath))
        {
            try
            {
                var root = FolderRoot(s, _roots == null ? Array.Empty<string>() : await _roots.AllAsync(ct)) ?? throw new StorageUnavailableException("The saved server folder is no longer allowed.");
                await new FolderObjectStore(root).DeletePrefixAsync(relativePrefix, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _log.LogError(ex, "Could not remove files under {Prefix} from the company's server folder", relativePrefix); problems.Add("your server folder"); }
        }
        if (problems.Count > 0)
            throw new StorageUnavailableException($"Files were removed from this server but could not be removed from {string.Join(" and ", problems)}.");
    }
}
