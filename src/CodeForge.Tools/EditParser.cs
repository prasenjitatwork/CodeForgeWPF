using System.Text.RegularExpressions;

namespace CodeForge.Tools;

internal sealed record EditBlock(string Path, string Search, string Replace);

/// <summary>
/// Parses SEARCH/REPLACE blocks from model output:
/// FILE: path / &lt;&lt;&lt;&lt;&lt;&lt;&lt; SEARCH / old / ======= / new / &gt;&gt;&gt;&gt;&gt;&gt;&gt; REPLACE.
/// Malformed blocks are reported, never guessed at.
/// </summary>
internal static partial class EditParser
{
    public const int MaxBlocks = 20;

    [GeneratedRegex(@"^\s*(?:[#*`>\-\s]*)(?:FILE|File|file|PATH|Path)\s*:\s*(?<p>.+?)\s*$")]
    private static partial Regex FileLine();

    [GeneratedRegex(@"^\s*<{5,}\s*SEARCH\s*$")]
    private static partial Regex Open();

    [GeneratedRegex(@"^\s*={5,}\s*$")]
    private static partial Regex Mid();

    [GeneratedRegex(@"^\s*>{5,}\s*REPLACE\s*$")]
    private static partial Regex Close();

    public static (List<EditBlock> Blocks, List<string> Problems) Parse(string text)
    {
        var blocks = new List<EditBlock>();
        var problems = new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string? path = null;
        string? previous = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (FileLine().Match(line) is { Success: true } f) { path = Clean(f.Groups["p"].Value); previous = null; continue; }
            if (!Open().IsMatch(line)) { if (line.Trim().Length > 0) previous = line; continue; }

            var blockPath = path ?? (previous is not null ? LooksLikePath(Clean(previous)) : null);
            var search = new List<string>();
            var replace = new List<string>();
            var stage = 0;
            var j = i + 1;
            for (; j < lines.Length; j++)
            {
                if (stage == 0 && Mid().IsMatch(lines[j])) { stage = 1; continue; }
                if (stage == 1 && Close().IsMatch(lines[j])) { stage = 2; break; }
                (stage == 0 ? search : replace).Add(lines[j]);
            }

            if (stage != 2) { problems.Add($"Incomplete SEARCH/REPLACE block near line {i + 1} was ignored."); break; }
            if (blockPath is null) problems.Add($"A SEARCH/REPLACE block near line {i + 1} has no file path and was ignored.");
            else if (blocks.Count >= MaxBlocks) { problems.Add($"Only the first {MaxBlocks} edit blocks are processed."); break; }
            else blocks.Add(new EditBlock(blockPath, string.Join('\n', search), string.Join('\n', replace)));
            i = j;
            previous = null;
        }
        return (blocks, problems);
    }

    private static string Clean(string s) => s.Trim().Trim('`', '*', '"', '\'', ' ').Replace('\\', '/');

    private static string? LooksLikePath(string s) =>
        s.Length is > 0 and < 260 && !s.Contains(' ') && s.Contains('.') ? s : null;
}
