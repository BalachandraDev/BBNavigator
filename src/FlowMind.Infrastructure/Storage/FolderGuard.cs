namespace FlowMind.Infrastructure.Storage;

/// <summary>
/// A company admin types a folder path and the server then writes files there. That must never reach system folders, other applications,
/// or anywhere the server operator did not approve, so a path is accepted only when it is an absolute path that lies INSIDE one of the roots
/// the operator listed in Storage:AllowedFolderRoots. No roots listed = folder storage is off.
/// </summary>
public static class FolderGuard
{
    private static readonly char[] Forbidden = { '*', '?', '"', '<', '>', '|' };
    private static StringComparison Cmp => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public const string OffMessage = "Folder storage is switched off on this server. Ask the platform administrator to approve a location first (Companies > Server folders, for example D:\\FlowMindDocs or \\\\fileserver\\FlowMind).";

    /// <returns>The full, normalised folder path when it may be used (<paramref name="error"/> is null); otherwise null and the reason in plain words.</returns>
    public static string? Check(string? input, IEnumerable<string>? roots, out string? error)
    {
        error = null;
        var allowed = (roots ?? Array.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).Select(Normalize).Where(r => r != null).Select(r => r!).Distinct().ToList();
        if (allowed.Count == 0) { error = OffMessage; return null; }

        var raw = (input ?? "").Trim();
        if (raw.Length == 0) { error = "Enter the folder path."; return null; }
        if (raw.Length > 300) { error = "The folder path is too long."; return null; }
        if (raw.Any(char.IsControl) || raw.IndexOfAny(Forbidden) >= 0) { error = "The folder path has characters that are not allowed."; return null; }
        if (!Path.IsPathFullyQualified(raw)) { error = "Enter the full path, for example D:\\FlowMindDocs or \\\\fileserver\\FlowMind (not a relative path)."; return null; }
        // ".." is refused outright rather than resolved, so what the admin sees is what is used.
        if (raw.Split('\\', '/').Any(part => part == "..")) { error = "The folder path must not contain \"..\"."; return null; }

        var full = Normalize(raw);
        if (full == null) { error = "That is not a valid folder path."; return null; }
        if (!allowed.Any(root => Inside(full, root)))
        {
            error = "That folder is not in a location this server allows. Allowed: " + string.Join("; ", allowed) + ".";
            return null;
        }
        return full;
    }

    /// <summary>True when <paramref name="full"/> is the root itself or lies below it ("D:\Docs2" is NOT inside "D:\Docs").</summary>
    public static bool Inside(string full, string root)
    {
        var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var f = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return f.Equals(r, Cmp) || f.StartsWith(r + Path.DirectorySeparatorChar, Cmp);
    }

    private static string? Normalize(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            var full = Path.GetFullPath(path);
            var rootOfPath = Path.GetPathRoot(full) ?? "";
            return full.Length > rootOfPath.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
