namespace CodeForge.Security;

/// <summary>
/// Confines all file access to a single project root. Every path a caller (UI, agent, model) supplies
/// must go through <see cref="Resolve"/>, which normalizes it and rejects traversal, other drives,
/// UNC/device paths, alternate data streams and symlink/junction escapes.
/// </summary>
public sealed class ProjectPathGuard
{
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public string Root { get; }

    public ProjectPathGuard(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Project root is required.", nameof(root));
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException($"Project folder not found: {Root}");
    }

    public bool IsWithinRoot(string fullPath)
    {
        var p = Path.TrimEndingDirectorySeparator(fullPath);
        return p.Equals(Root, Comparison) || p.StartsWith(Root + Path.DirectorySeparatorChar, Comparison);
    }

    /// <summary>Returns the absolute path for a root-relative (or absolute inside-root) path, or throws.</summary>
    public string Resolve(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Root;
        if (path.Contains('\0') || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new PathAccessException("The path contains invalid characters.");
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new PathAccessException("UNC and device paths are not allowed.");

        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PathAccessException("The path is not valid.");
        }

        if (!IsWithinRoot(full))
            throw new PathAccessException("The path is outside the project folder.");

        var rel = Path.GetRelativePath(Root, full);
        if (rel.Contains(':'))
            throw new PathAccessException("Alternate data streams are not allowed.");

        EnsureNoEscapingLinks(rel);
        return full;
    }

    public string ToRelative(string fullPath) => Path.GetRelativePath(Root, fullPath);

    /// <summary>True for symlinks and junctions (not for e.g. cloud-file placeholders).</summary>
    public static bool IsLink(FileSystemInfo info)
    {
        try { return info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null; }
        catch (IOException) { return true; }
    }

    private void EnsureNoEscapingLinks(string relative)
    {
        var current = Root;
        foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists) break;
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

            FileSystemInfo? target;
            try { target = info.ResolveLinkTarget(returnFinalTarget: true); }
            catch (IOException) { throw new PathAccessException("A link in the path could not be resolved."); }
            if (target is not null && !IsWithinRoot(target.FullName))
                throw new PathAccessException("The path follows a link that leaves the project folder.");
        }
    }
}
