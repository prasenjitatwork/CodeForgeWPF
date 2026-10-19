using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CodeForge.Application;

namespace CodeForge.Tools;

/// <summary>Builds a deterministic, bounded text overview of a project from its structure and config files.</summary>
internal static partial class ProjectInspector
{
    private static readonly string[] ConfigFiles =
    {
        "global.json", "Directory.Build.props", "Directory.Packages.props", "nuget.config", ".editorconfig",
        "package.json", "pubspec.yaml", "analysis_options.yaml", "Dockerfile", "docker-compose.yml", "Makefile",
        "README.md", "LICENSE", ".gitignore", "tsconfig.json", "pyproject.toml", "requirements.txt", "go.mod", "Cargo.toml"
    };

    [GeneratedRegex(@"^Project\(""\{[^}]+\}""\)\s*=\s*""(?<name>[^""]+)""\s*,\s*""(?<path>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex SlnProject();

    public static string Summarize(ProjectSession s, int maxChars)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Project: {s.Info.Name} ({s.Info.Kind})");
        sb.AppendLine($"Root: {s.Info.RootPath}");
        sb.AppendLine($"Files indexed: {s.Files.Count}{(s.Truncated ? " (index truncated)" : "")}");

        var byExt = s.Files.GroupBy(f => Path.GetExtension(f.RelativePath).ToLowerInvariant())
            .Where(g => g.Key.Length > 0).OrderByDescending(g => g.Count()).Take(8)
            .Select(g => $"{g.Key} {g.Count()}");
        sb.AppendLine("Top file types: " + string.Join(", ", byExt));

        try
        {
            var top = s.List("");
            sb.AppendLine("Top level: " + string.Join(", ", top.Take(30).Select(e => e.IsDirectory ? e.Name + "/" : e.Name)));
        }
        catch (ProjectException) { }

        var present = ConfigFiles.Where(c => s.Files.Any(f => f.RelativePath.Equals(c, StringComparison.OrdinalIgnoreCase))).ToList();
        if (present.Count > 0) sb.AppendLine("Config files: " + string.Join(", ", present));

        AppendSolution(sb, s);
        AppendCsprojs(sb, s);
        AppendPubspec(sb, s);
        AppendReadme(sb, s);

        var text = sb.ToString().TrimEnd();
        return text.Length <= maxChars ? text : text[..maxChars] + "\n…(truncated)";
    }

    private static void AppendSolution(StringBuilder sb, ProjectSession s)
    {
        var entry = s.Info.EntryFile;
        if (s.Info.Kind != ProjectKind.Solution || entry is null) return;
        var rel = s.Guard.ToRelative(entry);
        var ext = Path.GetExtension(entry).ToLowerInvariant();
        try
        {
            var projects = new List<string>();
            if (ext == ".sln")
            {
                var lines = s.ReadAllLinesOrNull(rel, 2_000_000);
                if (lines is not null)
                    foreach (Match m in SlnProject().Matches(string.Join('\n', lines)))
                        if (m.Groups["path"].Value.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
                            projects.Add(m.Groups["path"].Value.Replace('\\', '/'));
            }
            else if (ext == ".slnx")
            {
                var doc = LoadXml(s, rel);
                if (doc is not null)
                    projects.AddRange(doc.Descendants("Project").Select(p => (string?)p.Attribute("Path")).OfType<string>());
            }
            if (projects.Count > 0)
                sb.AppendLine($"Solution {Path.GetFileName(entry)} projects: " + string.Join(", ", projects.Take(40)));
        }
        catch (Exception ex) when (ex is XmlException or IOException) { /* best effort */ }
    }

    private static void AppendCsprojs(StringBuilder sb, ProjectSession s)
    {
        var projects = s.Files.Where(f => f.RelativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).Take(15).ToList();
        foreach (var p in projects)
        {
            var doc = LoadXml(s, p.RelativePath);
            if (doc is null) continue;
            string Prop(string n) => doc.Descendants(n).Select(e => e.Value.Trim()).FirstOrDefault() ?? "";
            var tfm = Prop("TargetFramework");
            if (tfm.Length == 0) tfm = Prop("TargetFrameworks");
            var packages = doc.Descendants("PackageReference").Select(e => (string?)e.Attribute("Include")).OfType<string>().Take(15);
            var refs = doc.Descendants("ProjectReference").Select(e => Path.GetFileNameWithoutExtension(((string?)e.Attribute("Include") ?? "").Replace('\\', '/'))).Take(10);
            sb.AppendLine($"- {p.RelativePath.Replace('\\', '/')}: tfm={tfm}, output={Prop("OutputType")}" +
                          (packages.Any() ? ", packages=" + string.Join("/", packages) : "") +
                          (refs.Any() ? ", refs=" + string.Join("/", refs) : ""));
        }
    }

    private static void AppendPubspec(StringBuilder sb, ProjectSession s)
    {
        var lines = s.ReadAllLinesOrNull("pubspec.yaml", 200_000);
        if (lines is null) return;
        string? Top(string key) => lines.FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal))?[(key.Length + 1)..].Trim();
        sb.AppendLine($"Pubspec: name={Top("name")}, description={Top("description")}");
        foreach (var section in new[] { "dependencies", "dev_dependencies" })
        {
            var idx = Array.FindIndex(lines, l => l.TrimEnd() == section + ":");
            if (idx < 0) continue;
            var names = new List<string>();
            for (var i = idx + 1; i < lines.Length && (lines[i].Length == 0 || char.IsWhiteSpace(lines[i][0]) || lines[i][0] == '#'); i++)
            {
                var l = lines[i];
                if (l.Length > 2 && l[0] == ' ' && l[1] == ' ' && !char.IsWhiteSpace(l[2]) && l[2] != '#')
                    names.Add(l.Trim().Split(':')[0]);
            }
            if (names.Count > 0) sb.AppendLine($"{section}: " + string.Join(", ", names.Take(25)));
        }
    }

    private static void AppendReadme(StringBuilder sb, ProjectSession s)
    {
        var readme = s.Files.FirstOrDefault(f => f.RelativePath.Equals("README.md", StringComparison.OrdinalIgnoreCase))?.RelativePath;
        var lines = readme is null ? null : s.ReadAllLinesOrNull(readme, 500_000);
        if (lines is null) return;
        sb.AppendLine("README excerpt:");
        foreach (var l in lines.Where(l => l.Trim().Length > 0).Take(12)) sb.AppendLine("  " + (l.Length > 200 ? l[..200] : l));
    }

    private static XDocument? LoadXml(ProjectSession s, string relativePath)
    {
        var lines = s.ReadAllLinesOrNull(relativePath, 1_000_000);
        if (lines is null) return null;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(string.Join('\n', lines)), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException) { return null; }
    }
}
