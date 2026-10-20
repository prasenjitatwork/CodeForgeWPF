using System.Text.RegularExpressions;

namespace CodeForge.Tools;

internal sealed record EditBlock(string Path, string Search, string Replace);

internal enum EditOpKind { Delete, Rename }

internal sealed record FileOp(EditOpKind Kind, string Path, string? NewPath);

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
        AddNewFileFallbacks(lines, blocks, problems);
        return (blocks, problems);
    }

    [GeneratedRegex(@"^\s*```")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\s*<{2,}>{2,}\s*$|^\s*<<<\s*>>>\s*$")]
    private static partial Regex LooseMarker();

    /// <summary>
    /// Small models often write "FILE: path" followed by a fenced code block (or loose &lt;&lt;&gt;&gt; markers) instead of a
    /// SEARCH/REPLACE block. Treat that only as a proposal to create a NEW file; existing files are never overwritten this way
    /// (the edit service rejects a create for a path that already exists).
    /// </summary>
    private static void AddNewFileFallbacks(string[] lines, List<EditBlock> blocks, List<string> problems)
    {
        var inBlock = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (Open().IsMatch(lines[i])) inBlock = true;
            else if (Close().IsMatch(lines[i])) { inBlock = false; continue; }
            if (inBlock || FileLine().Match(lines[i]) is not { Success: true } f) continue;
            var path = Clean(f.Groups["p"].Value);
            if (LooksLikePath(path) is null || blocks.Any(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

            var k = i + 1;
            while (k < lines.Length && lines[k].Trim().Length == 0) k++;
            if (k >= lines.Length) continue;
            Regex opener = Fence().IsMatch(lines[k]) ? Fence() : LooseMarker().IsMatch(lines[k]) ? LooseMarker() : null!;
            if (opener is null) continue;

            var body = new List<string>();
            var end = k + 1;
            for (; end < lines.Length; end++)
            {
                if (opener.IsMatch(lines[end])) break;
                body.Add(lines[end]);
            }
            if (end >= lines.Length) { problems.Add($"The file content for '{path}' was not closed and was ignored."); continue; }
            if (blocks.Count >= MaxBlocks) { problems.Add($"Only the first {MaxBlocks} edit blocks are processed."); break; }
            blocks.Add(new EditBlock(path, "", string.Join('\n', body)));
            i = end;
        }
    }

    private static string Clean(string s) => s.Trim().Trim('`', '*', '"', '\'', ' ').Replace('\\', '/');

    [GeneratedRegex(@"^\s*(?:[#*`>\-\s]*)(?<op>DELETE|RENAME)\s*:\s*(?<rest>\S.*?)\s*$")]
    private static partial Regex OpLine();

    public const int MaxOps = 5;

    private static readonly string[] Arrows = { "->", "→", "=>" };

    /// <summary>Finds "DELETE: path" and "RENAME: old -> new" lines outside SEARCH/REPLACE blocks. Malformed lines are reported.</summary>
    public static (List<FileOp> Ops, List<string> Problems) ParseOps(string text)
    {
        var ops = new List<FileOp>();
        var problems = new List<string>();
        var inBlock = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (Open().IsMatch(line)) { inBlock = true; continue; }
            if (inBlock) { if (Close().IsMatch(line)) inBlock = false; continue; }
            var m = OpLine().Match(line);
            if (!m.Success) continue;
            var isRename = m.Groups["op"].Value == "RENAME";
            var rest = m.Groups["rest"].Value;
            string a = rest, b = "";
            foreach (var arrow in Arrows)
            {
                var i = rest.IndexOf(arrow, StringComparison.Ordinal);
                if (i < 0) continue;
                (a, b) = (rest[..i], rest[(i + arrow.Length)..]);
                break;
            }
            a = Clean(a); b = Clean(b);
            if (isRename && (a.Length == 0 || b.Length == 0)) { problems.Add($"RENAME needs 'old/path -> new/path': \"{line.Trim()}\" was ignored."); continue; }
            if (!isRename && b.Length > 0) { problems.Add($"DELETE takes a single path: \"{line.Trim()}\" was ignored."); continue; }
            if (ops.Count >= MaxOps) { problems.Add($"Only the first {MaxOps} delete/rename operations are processed."); break; }
            ops.Add(new FileOp(isRename ? EditOpKind.Rename : EditOpKind.Delete, a, isRename ? b : null));
        }
        return (ops, problems);
    }

    private static string? LooksLikePath(string s) =>
        s.Length is > 0 and < 260 && !s.Contains(' ') && s.Contains('.') ? s : null;
}
