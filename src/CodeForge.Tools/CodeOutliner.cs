using System.Text.RegularExpressions;
using CodeForge.Application;

namespace CodeForge.Tools;

/// <summary>Heuristic (regex) declaration outline for C# and Dart. Not a full parser.</summary>
public static partial class CodeOutliner
{
    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|unsafe|file|new)\s+)*(?<kind>class|interface|struct|enum|record\s+struct|record\s+class|record)\s+(?<name>[A-Za-z_]\w*)")]
    private static partial Regex CsType();

    [GeneratedRegex(@"^\s*namespace\s+(?<name>[\w.]+)")]
    private static partial Regex CsNamespace();

    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|static|async|virtual|override|abstract|sealed|extern|unsafe|new)\s+)+(?:[\w<>\[\],.?]+\s+)+(?<name>[A-Za-z_]\w*)\s*(?:<[^>(]*>)?\s*\(")]
    private static partial Regex CsMethod();

    [GeneratedRegex(@"^\s*(?:(?:abstract|base|sealed|final|interface|mixin)\s+)*(?<kind>class|mixin|enum|extension)\s+(?<name>[A-Za-z_]\w*)")]
    private static partial Regex DartType();

    [GeneratedRegex(@"^(?:[\w<>?,\[\]]+\s+)(?<name>[A-Za-z_]\w*)\s*\([^;]*$")]
    private static partial Regex DartTopFunction();

    public static IReadOnlyList<Declaration> Outline(string extension, IReadOnlyList<string> lines)
    {
        var result = new List<Declaration>();
        var ext = extension.ToLowerInvariant();
        for (var i = 0; i < lines.Count && result.Count < 400; i++)
        {
            var line = lines[i];
            if (ext == ".cs")
            {
                if (CsNamespace().Match(line) is { Success: true } ns) result.Add(new("namespace", ns.Groups["name"].Value, i + 1));
                else if (CsType().Match(line) is { Success: true } t) result.Add(new(Regex.Replace(t.Groups["kind"].Value, @"\s+", " "), t.Groups["name"].Value, i + 1));
                else if (CsMethod().Match(line) is { Success: true } m && (!line.TrimEnd().EndsWith(';') || line.Contains("=>"))) result.Add(new("member", m.Groups["name"].Value, i + 1));
            }
            else if (ext == ".dart")
            {
                if (DartType().Match(line) is { Success: true } t) result.Add(new(t.Groups["kind"].Value, t.Groups["name"].Value, i + 1));
                else if (DartTopFunction().Match(line) is { Success: true } f) result.Add(new("function", f.Groups["name"].Value, i + 1));
            }
        }
        return result;
    }

    public static bool IsSupported(string extension) =>
        extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dart", StringComparison.OrdinalIgnoreCase);
}
