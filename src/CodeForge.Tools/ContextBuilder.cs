using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CodeForge.Application;
using CodeForge.Security;

namespace CodeForge.Tools;

/// <summary>Deterministic keyword retrieval: picks a few relevant files and bounded excerpts instead of sending the repository.</summary>
internal static partial class ContextBuilder
{
    public const int TotalBudget = 12_000;
    public const int PerFileBudget = 3_500;
    public const int MaxFiles = 4;
    private const int MaxScannedFiles = 1500;
    private const long MaxScanBytes = 300_000;

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","for","with","this","that","what","how","why","where","which","when","does","are","can","you","please",
        "show","explain","tell","about","from","into","there","here","have","has","had","use","used","using","make","code",
        "file","files","class","method","function","project","find","give","write","help","need","want","would","could","should",
        "our","your","its","not","but","all","any","some","them","they","then","than","also","just","like","more","most","does","did"
    };

    private static readonly HashSet<string> SourceExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".dart", ".csproj", ".json", ".yaml", ".yml", ".md", ".ts", ".tsx", ".js", ".py", ".java", ".kt", ".xml", ".props", ".sln", ".slnx", ".sql", ".razor", ".cshtml"
    };

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]{2,}")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"[\w./\\-]+\.[A-Za-z0-9]{1,6}\b")]
    private static partial Regex FileToken();

    public static IReadOnlyList<string> ExtractKeywords(string query) =>
        Identifier().Matches(query).Select(m => m.Value.ToLowerInvariant())
            .Where(k => !Stop.Contains(k)).Distinct().Take(12).ToList();

    public static ProjectContext Build(ProjectSession s, string query, string summary, CancellationToken ct)
    {
        var keywords = ExtractKeywords(query);
        var fileTokens = FileToken().Matches(query).Select(m => m.Value.Replace('\\', '/').ToLowerInvariant()).Distinct().ToList();

        var candidates = s.Files
            .Where(f => f.Size is > 0 and <= MaxScanBytes && !ProjectFilePolicy.IsBinaryExtension(Path.GetExtension(f.RelativePath)))
            .Select(f => (File: f, Name: NameScore(f.RelativePath, keywords, fileTokens)))
            .OrderByDescending(x => x.Name)
            .ThenByDescending(x => SourceExt.Contains(Path.GetExtension(x.File.RelativePath)) ? 1 : 0)
            .Take(MaxScannedFiles)
            .ToList();

        var scored = new List<(string Rel, int Score, string[] Lines, List<int> Hits, string Reason)>();
        var clock = Stopwatch.StartNew();
        foreach (var (file, nameScore) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (clock.ElapsedMilliseconds > 3000 && nameScore == 0) break;
            if (keywords.Count == 0 && nameScore == 0) break;

            var lines = s.ReadAllLinesOrNull(file.RelativePath, MaxScanBytes);
            if (lines is null) continue;
            var hits = new List<int>();
            var distinct = new HashSet<string>();
            var total = 0;
            for (var i = 0; i < lines.Length; i++)
                foreach (var k in keywords)
                    if (lines[i].Contains(k, StringComparison.OrdinalIgnoreCase))
                    {
                        total++;
                        distinct.Add(k);
                        if (hits.Count < 40 && (hits.Count == 0 || hits[^1] != i + 1)) hits.Add(i + 1);
                    }
            var score = nameScore + Math.Min(total, 15) * 2 + distinct.Count * 3;
            if (score >= 3)
                scored.Add((file.RelativePath, score, lines, hits,
                    nameScore > 0 ? "file name matches" : "contains " + string.Join(", ", distinct.Take(4))));
        }

        // Local term index: finds code described with different words (delete/remove, customer/client, ...).
        var terms = ProjectIndex.ExpandQuery(query);
        if (terms.Count > 0)
        {
            var ranked = s.GetIndex(ct).Search(terms, 8);
            var max = ranked.Count == 0 ? 1 : ranked[0].Score;
            foreach (var (path, sc) in ranked)
            {
                ct.ThrowIfCancellationRequested();
                var bonus = (int)Math.Round(sc / max * 14) + 2;
                var i = scored.FindIndex(x => x.Rel == path);
                if (i >= 0)
                {
                    var e = scored[i];
                    scored[i] = e with { Score = e.Score + bonus, Reason = e.Reason + "; related terms" };
                    continue;
                }
                var lines = s.ReadAllLinesOrNull(path, MaxScanBytes);
                if (lines is null) continue;
                var hits = new List<int>();
                var matched = new HashSet<string>();
                for (var n = 0; n < lines.Length && hits.Count < 40; n++)
                    foreach (var t in ProjectIndex.Tokenize(lines[n]))
                        if (terms.ContainsKey(t)) { hits.Add(n + 1); matched.Add(t); break; }
                var via = terms.Where(kv => matched.Contains(kv.Key) && kv.Value.Via is not null).Select(kv => kv.Value.Via!).Distinct().Take(3).ToList();
                scored.Add((path, bonus, lines, hits,
                    via.Count > 0 ? "related wording (similar to: " + string.Join(", ", via) + ")" : "index match"));
            }
        }

        var chosen = scored.OrderByDescending(x => x.Score).Take(MaxFiles).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("Project overview:").AppendLine(summary).AppendLine();

        var used = new List<string>();
        var details = new List<ContextExcerpt>();
        var remaining = TotalBudget;
        if (chosen.Count > 0) sb.AppendLine("Relevant excerpts (retrieved automatically by keyword and local index match):");
        foreach (var c in chosen)
        {
            if (remaining < 300) break;
            var excerpt = Excerpt(c.Lines, c.Hits, Math.Min(PerFileBudget, remaining));
            var rel = c.Rel.Replace('\\', '/');
            sb.Append("=== FILE ").Append(rel).Append(" (").Append(excerpt.Range).AppendLine(") ===");
            sb.AppendLine(excerpt.Text).AppendLine("=== END FILE ===");
            remaining -= excerpt.Text.Length;
            used.Add(rel);
            details.Add(new ContextExcerpt(rel, excerpt.Range, c.Reason, excerpt.Text));
        }
        return new ProjectContext(sb.ToString().TrimEnd(), used, details);
    }

    private static int NameScore(string rel, IReadOnlyList<string> keywords, IReadOnlyList<string> fileTokens)
    {
        var norm = rel.Replace('\\', '/').ToLowerInvariant();
        var name = Path.GetFileName(norm);
        var stem = Path.GetFileNameWithoutExtension(norm);
        var score = 0;
        foreach (var t in fileTokens)
            if (norm == t || norm.EndsWith("/" + t, StringComparison.Ordinal)) score += 50;
        foreach (var k in keywords)
        {
            if (stem == k) score += 20;
            else if (name.Contains(k, StringComparison.Ordinal)) score += 10;
            else if (norm.Contains(k, StringComparison.Ordinal)) score += 4;
        }
        return score;
    }

    private static (string Text, string Range) Excerpt(string[] lines, List<int> hits, int budget)
    {
        if (lines.Length == 0) return (string.Empty, "empty");
        var windows = new List<(int Start, int End)>();
        if (hits.Count == 0) windows.Add((1, Math.Min(lines.Length, 80)));
        else
            foreach (var h in hits)
            {
                var (s, e) = (Math.Max(1, h - 6), Math.Min(lines.Length, h + 6));
                if (windows.Count > 0 && s <= windows[^1].End + 1) windows[^1] = (windows[^1].Start, Math.Max(windows[^1].End, e));
                else windows.Add((s, e));
            }

        var sb = new StringBuilder();
        var first = 0;
        var last = 0;
        foreach (var (s, e) in windows)
        {
            var chunk = new StringBuilder();
            for (var i = s; i <= e; i++) chunk.Append(lines[i - 1]).Append('\n');
            if (sb.Length > 0 && sb.Length + chunk.Length > budget) break;
            if (sb.Length > 0) sb.Append("…\n");
            sb.Append(chunk);
            if (first == 0) first = s;
            last = e;
            if (sb.Length >= budget) break;
        }
        var text = sb.ToString().TrimEnd('\n');
        if (text.Length > budget) text = text[..budget] + "…";
        return (text.Replace("</project_context>", "< /project_context>"), $"lines {first}-{last}");
    }
}
