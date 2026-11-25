using FlowMind.Domain.Entities;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.Storage;

/// <summary>
/// The locations a company may pick for "Folder on a server": the roots the server operator set in configuration (Storage:AllowedFolderRoots)
/// plus the locations the platform administrator approved in the app (Companies > Server folders). Company admins can never add to this list.
/// </summary>
public sealed class FolderRoots
{
    private readonly FlowMindDbContext _db;
    private readonly string[] _configured;

    public FolderRoots(FlowMindDbContext db, IOptions<StorageOptions>? options = null)
    { _db = db; _configured = options?.Value.AllowedFolderRoots ?? Array.Empty<string>(); }

    public async Task<IReadOnlyList<string>> AllAsync(CancellationToken ct)
    {
        var approved = await _db.AllowedFolders.AsNoTracking().OrderBy(f => f.Path).Select(f => f.Path).ToListAsync(ct);
        return _configured.Where(r => !string.IsNullOrWhiteSpace(r)).Concat(approved).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Checks a location the platform admin wants to approve. It must be a full path, must already exist, and must not be a whole drive or a system folder.</summary>
    public static string? CheckLocation(string? input, out string? error)
    {
        var raw = (input ?? "").Trim();
        if (raw.Length > 0 && !Path.IsPathFullyQualified(raw))
        {
            var windowsStyle = raw.Length >= 2 && char.IsLetter(raw[0]) && raw[1] == ':' || raw.StartsWith(@"\\", StringComparison.Ordinal);
            error = windowsStyle && !OperatingSystem.IsWindows()
                ? "This server runs on Linux (inside Docker), so a Windows path like " + raw + " cannot be used here. Use the folder the containers share, /shared-docs, or mount your Windows folder into Docker (STORAGE_HOST_FOLDER in .env) and approve /shared-docs."
                : "Enter the full path, for example D:\\FlowMindDocs or \\\\fileserver\\FlowMind (not a relative path).";
            return null;
        }
        var full = FolderGuard.Check(raw, new[] { raw }, out error);   // path rules only (any full path is "inside" itself)
        if (full == null) return null;
        var root = Path.GetPathRoot(full) ?? "";
        if (string.Equals(full.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        { error = "Choose a folder, not a whole drive. Use a folder made for FlowMind, for example D:\\FlowMindDocs."; return null; }
        if (IsSystemFolder(full)) { error = "That is a system or program folder. Choose a folder made for FlowMind."; return null; }
        if (!Directory.Exists(full))
        { error = "That folder was not found from this server. Create it first, check the spelling, and for a network share make sure this server can reach it."; return null; }
        error = null;
        return full;
    }

    private static readonly string[] SystemNames = { "windows", "program files", "program files (x86)", "programdata", "inetpub", "etc", "bin", "sbin", "usr", "var", "proc", "sys", "boot", "root" };

    private static bool IsSystemFolder(string full)
    {
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;                    // a file share: its own server decides
        var parts = full.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        var first = parts.Skip(OperatingSystem.IsWindows() ? 1 : 0).FirstOrDefault();          // the first folder below the drive (or "/"), e.g. C:\Windows or /etc
        return first != null && SystemNames.Contains(first.ToLowerInvariant());
    }
}