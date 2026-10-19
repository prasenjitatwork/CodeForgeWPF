using CodeForge.Application;

namespace CodeForge.Tools;

/// <summary>Maps a user-selected folder/.sln/.slnx/.csproj/pubspec.yaml to a project root.</summary>
public static class ProjectLocator
{
    public static ProjectInfo Locate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ProjectException("Select a folder, .sln, .slnx, .csproj or pubspec.yaml.");
        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProjectException("The selected path is not valid.", ex);
        }

        if (Directory.Exists(full))
        {
            full = Path.TrimEndingDirectorySeparator(full);
            if (string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase))
                throw new ProjectException("Select a project folder, not a drive root.");
            var name = Path.GetFileName(full);
            var pubspec = Path.Combine(full, "pubspec.yaml");
            if (File.Exists(pubspec) && IsFlutter(pubspec))
                return new ProjectInfo(name, full, ProjectKind.Flutter, pubspec);
            var sln = FirstFile(full, "*.sln") ?? FirstFile(full, "*.slnx");
            if (sln is not null) return new ProjectInfo(name, full, ProjectKind.Solution, sln);
            var csproj = FirstFile(full, "*.csproj");
            if (csproj is not null) return new ProjectInfo(name, full, ProjectKind.CSharpProject, csproj);
            return new ProjectInfo(name, full, ProjectKind.Folder, null);
        }

        if (!File.Exists(full)) throw new ProjectException($"Not found: {full}");
        var dir = Path.GetDirectoryName(full)!;
        var file = Path.GetFileName(full);
        var ext = Path.GetExtension(full).ToLowerInvariant();
        if (ext is ".sln" or ".slnx") return new ProjectInfo(Path.GetFileNameWithoutExtension(full), dir, ProjectKind.Solution, full);
        if (ext == ".csproj") return new ProjectInfo(Path.GetFileNameWithoutExtension(full), dir, ProjectKind.CSharpProject, full);
        if (file.Equals("pubspec.yaml", StringComparison.OrdinalIgnoreCase))
            return new ProjectInfo(Path.GetFileName(dir), dir, IsFlutter(full) ? ProjectKind.Flutter : ProjectKind.Folder, full);
        throw new ProjectException("Unsupported file. Select a folder, .sln, .slnx, .csproj or pubspec.yaml.");
    }

    private static string? FirstFile(string dir, string pattern) =>
        Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    private static bool IsFlutter(string pubspec)
    {
        try
        {
            using var r = new StreamReader(pubspec);
            for (var i = 0; i < 400 && r.ReadLine() is { } line; i++)
                if (line.Contains("sdk: flutter", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("flutter:", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (IOException) { }
        return false;
    }
}
