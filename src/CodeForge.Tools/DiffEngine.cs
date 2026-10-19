using CodeForge.Application;

namespace CodeForge.Tools;

/// <summary>Line-based diff (common prefix/suffix trim + LCS) with unified-style context collapsing.</summary>
internal static class DiffEngine
{
    private const int MaxCells = 4_000_000;
    private const int ContextLines = 3;

    public static (IReadOnlyList<DiffLine> Lines, int Added, int Removed) Diff(string oldText, string newText)
    {
        var a = Split(oldText);
        var b = Split(newText);
        var all = new List<DiffLine>();

        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        var endA = a.Length;
        var endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }

        for (var i = 0; i < start; i++) all.Add(new(DiffKind.Context, a[i], i + 1, i + 1));

        var n = endA - start;
        var m = endB - start;
        if ((long)n * m <= MaxCells && n > 0 && m > 0)
        {
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[start + i] == b[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[start + x] == b[start + y])
                { all.Add(new(DiffKind.Context, a[start + x], start + x + 1, start + y + 1)); x++; y++; }
                else if (y < m && (x == n || lcs[x, y + 1] > lcs[x + 1, y]))
                { all.Add(new(DiffKind.Added, b[start + y], null, start + y + 1)); y++; }
                else
                { all.Add(new(DiffKind.Removed, a[start + x], start + x + 1, null)); x++; }
            }
        }
        else
        {
            for (var i = 0; i < n; i++) all.Add(new(DiffKind.Removed, a[start + i], start + i + 1, null));
            for (var j = 0; j < m; j++) all.Add(new(DiffKind.Added, b[start + j], null, start + j + 1));
        }

        for (var k = 0; k < a.Length - endA; k++) all.Add(new(DiffKind.Context, a[endA + k], endA + k + 1, endB + k + 1));

        var added = all.Count(l => l.Kind == DiffKind.Added);
        var removed = all.Count(l => l.Kind == DiffKind.Removed);
        return (Collapse(all), added, removed);
    }

    private static string[] Split(string s)
    {
        if (s.Length == 0) return Array.Empty<string>();
        var lines = s.Replace("\r\n", "\n").Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static IReadOnlyList<DiffLine> Collapse(List<DiffLine> all)
    {
        var keep = new bool[all.Count];
        for (var i = 0; i < all.Count; i++)
            if (all[i].Kind != DiffKind.Context)
                for (var k = Math.Max(0, i - ContextLines); k <= Math.Min(all.Count - 1, i + ContextLines); k++) keep[k] = true;

        var result = new List<DiffLine>();
        var gap = false;
        for (var i = 0; i < all.Count; i++)
        {
            if (keep[i])
            {
                if (gap && result.Count > 0) result.Add(new(DiffKind.Gap, "⋯", null, null));
                gap = false;
                result.Add(all[i]);
            }
            else gap = true;
        }
        return result;
    }
}
