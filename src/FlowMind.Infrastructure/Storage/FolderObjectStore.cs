using System.Diagnostics;

namespace FlowMind.Infrastructure.Storage;

/// <summary>
/// An <see cref="IObjectStore"/> over a folder on a server the app can reach: a drive on the IIS machine, or a file share such as \\fileserver\FlowMind.
/// The app's own account (on IIS, the application pool identity) does the reading and writing, so that account needs Modify permission on the folder.
/// Every key is resolved against the root and must stay inside it. The folder itself must already exist: it is never created, so a typo cannot
/// silently create a stray folder on a server.
/// </summary>
public sealed class FolderObjectStore : IObjectStore
{
    private readonly string _root;

    /// <param name="root">An already-validated full folder path (see <see cref="FolderGuard"/>).</param>
    public FolderObjectStore(string root) => _root = root;

    private string Resolve(string key)
    {
        var rel = (key ?? "").Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!FolderGuard.Inside(full, _root)) throw new InvalidOperationException("Path escapes the storage folder.");
        return full;
    }

    public async Task PutFileAsync(string key, string filePath, CancellationToken ct)
    {
        var target = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + "." + Guid.NewGuid().ToString("N")[..8] + ".part";      // written beside the target, then renamed, so a half-written file is never read
        try
        {
            await using (var src = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var dst = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await src.CopyToAsync(dst, 81920, ct);
            File.Move(part, target, overwrite: true);
        }
        catch { try { if (File.Exists(part)) File.Delete(part); } catch { /* best effort */ } throw; }
    }

    public Task<Stream> GetAsync(string key, CancellationToken ct) =>
        Task.FromResult<Stream>(new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var f = Resolve(key);
        if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    public Task DeletePrefixAsync(string prefix, CancellationToken ct)
    {
        var dir = Resolve(prefix);
        if (!FolderGuard.Inside(dir, _root) || string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), _root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to remove the storage folder itself.");   // only ever a company's or a document's own subfolder
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>Checks the folder exists, then writes, reads back and removes a tiny file, saying in plain words what is wrong if any step fails.</summary>
    public async Task<StorageProbeResult> ProbeAsync(string keyPrefix, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string? probeFile = null;
        try
        {
            if (!Directory.Exists(_root))
                return new StorageProbeResult(false, "That folder was not found from this server. Create it on the server first, check the spelling, and for a network share make sure this server can reach it.", sw.ElapsedMilliseconds);
            probeFile = Path.Combine(_root, ".flowmind-test-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            var data = "flowmind storage test " + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(probeFile, data, ct);
            var back = await File.ReadAllTextAsync(probeFile, ct);
            File.Delete(probeFile); probeFile = null;
            return back == data
                ? new StorageProbeResult(true, "The folder works: a test file was written, read back and removed.", sw.ElapsedMilliseconds)
                : new StorageProbeResult(false, "The test file was written but read back differently. Check the folder or share.", sw.ElapsedMilliseconds);
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageProbeResult(false, "The account this app runs as is not allowed to write to that folder. Give it Modify permission (on IIS, the application pool identity; for a share, both the share and the folder permissions).", sw.ElapsedMilliseconds);
        }
        catch (DirectoryNotFoundException) { return new StorageProbeResult(false, "That folder was not found from this server.", sw.ElapsedMilliseconds); }
        catch (IOException ex)
        {
            return new StorageProbeResult(false, "The folder could not be used: " + ex.Message, sw.ElapsedMilliseconds);
        }
        finally { try { if (probeFile != null && File.Exists(probeFile)) File.Delete(probeFile); } catch { /* best effort */ } }
    }
}
