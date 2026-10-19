using System.Text.RegularExpressions;
using CodeForge.Application;

namespace CodeForge.Tools;

internal static partial class BuildOutputParser
{
    public const int MaxDiagnostics = 50;

    [GeneratedRegex(@"^(?<file>.+?)\((?<line>\d+)(?:,\d+)?(?:,\d+,\d+)?\):\s*(?<sev>error|warning)\s+(?<code>\w+):\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MsBuildFile();

    [GeneratedRegex(@"^(?:.*?:\s*)?(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+):\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MsBuildGeneral();

    [GeneratedRegex(@"^\s*(?<sev>error|warning)\s+•\s+(?<msg>.+?)\s+•\s+(?<file>.+?):(?<line>\d+):\d+\s+•", RegexOptions.IgnoreCase)]
    private static partial Regex FlutterAnalyze();

    [GeneratedRegex(@"^\s*Failed\s+(?<name>[\w.<>`+,\[\]\(\)-]+)")]
    private static partial Regex TestFailed();

    public static IReadOnlyList<BuildDiagnostic> Parse(string output, string root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<BuildDiagnostic>();
        foreach (var raw in output.Split('\n'))
        {
            if (list.Count >= MaxDiagnostics) break;
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.Length > 2000) continue;
            BuildDiagnostic? d = null;

            var m = MsBuildFile().Match(line);
            if (m.Success)
                d = new(Sev(m), Rel(m.Groups["file"].Value, root), int.Parse(m.Groups["line"].Value), $"{m.Groups["code"].Value}: {m.Groups["msg"].Value}");
            else if ((m = FlutterAnalyze().Match(line)).Success)
                d = new(Sev(m), Rel(m.Groups["file"].Value, root), int.Parse(m.Groups["line"].Value), m.Groups["msg"].Value);
            else if ((m = MsBuildGeneral().Match(line)).Success)
                d = new(Sev(m), null, 0, $"{m.Groups["code"].Value}: {m.Groups["msg"].Value}");
            else if ((m = TestFailed().Match(line)).Success)
                d = new(DiagnosticSeverity.Error, null, 0, "Test failed: " + m.Groups["name"].Value);

            if (d is not null && seen.Add($"{d.Severity}|{d.File}|{d.Line}|{d.Message}")) list.Add(d);
        }
        return list;
    }

    private static DiagnosticSeverity Sev(Match m) =>
        m.Groups["sev"].Value.Equals("error", StringComparison.OrdinalIgnoreCase) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;

    private static string Rel(string file, string root)
    {
        file = file.Trim();
        try
        {
            if (Path.IsPathRooted(file))
            {
                var r = Path.GetRelativePath(root, file);
                if (!r.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(r)) return r.Replace('\\', '/');
            }
        }
        catch (ArgumentException) { }
        return file.Replace('\\', '/');
    }
}
