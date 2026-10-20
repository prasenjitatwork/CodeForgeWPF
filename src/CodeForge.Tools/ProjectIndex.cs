using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeForge.Security;

namespace CodeForge.Tools;

/// <summary>
/// Small in-memory, local-only term index over the project's readable text files (BM25 ranking over identifier
/// sub-words). It lets a question like "where do we remove a customer" find <c>DeleteClient</c>. It is built from the
/// same policy-filtered reader as everything else, so excluded folders and secret files never enter it.
/// </summary>
internal sealed partial class ProjectIndex
{
    public const int MaxFiles = 1500;
    public const long MaxFileBytes = 300_000;
    public const int BuildBudgetMs = 4000;

    private sealed record Doc(string Path, Dictionary<string, int> Terms, int Length);

    private readonly List<Doc> _docs = new();
    private readonly Dictionary<string, int> _docFreq = new();
    private double _avgLen = 1;

    public int FileCount => _docs.Count;
    public bool Complete { get; private set; } = true;

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","for","with","this","that","what","how","why","where","which","when","does","are","can","you","please",
        "show","explain","tell","about","from","into","there","here","have","has","had","use","used","using","make","code",
        "file","files","class","method","function","project","find","give","write","help","need","want","would","could","should",
        "our","your","its","not","but","all","any","some","them","they","then","than","also","just","like","more","most","did",
        "var","int","string","void","public","private","static","return","new","null","true","false","get","set","namespace","using"
    };

    // Groups of words people use interchangeably when describing behaviour. Matching is by stem.
    private static readonly string[][] SynonymGroups =
    {
        new[] { "delete", "remove", "erase", "destroy", "drop", "clear" },
        new[] { "create", "add", "insert", "new", "register", "append" },
        new[] { "save", "persist", "store", "write", "commit", "serialize" },
        new[] { "load", "read", "fetch", "retrieve", "open", "restore", "deserialize" },
        new[] { "error", "exception", "fail", "failure", "fault", "crash", "problem", "invalid" },
        new[] { "login", "signin", "auth", "authenticate", "authentication", "credential", "session", "logon" },
        new[] { "logout", "signout", "logoff" },
        new[] { "user", "account", "profile", "customer", "member", "client" },
        new[] { "password", "credential", "passphrase", "secret" },
        new[] { "send", "post", "request", "http", "submit", "upload" },
        new[] { "database", "db", "sql", "sqlite", "repository", "storage", "table", "query" },
        new[] { "config", "configuration", "setting", "option", "preference", "parameter" },
        new[] { "log", "logger", "logging", "trace", "diagnostic" },
        new[] { "search", "find", "lookup", "filter", "locate", "match" },
        new[] { "show", "display", "render", "view", "present", "draw", "screen", "window" },
        new[] { "button", "click", "command", "press", "action" },
        new[] { "build", "compile", "msbuild", "make" },
        new[] { "test", "spec", "assert", "verify", "check", "validate", "validation" },
        new[] { "parse", "parser", "tokenize", "decode", "extract" },
        new[] { "permission", "access", "allow", "authorize", "authorization", "grant", "policy", "guard" },
        new[] { "theme", "color", "colour", "style", "brush", "dark" },
        new[] { "memory", "note", "remember", "recall" },
        new[] { "plan", "step", "task", "todo" },
        new[] { "edit", "change", "modify", "update", "patch", "replace", "diff" },
        new[] { "rename", "move", "relocate" },
        new[] { "price", "cost", "total", "amount", "sum", "invoice", "bill", "payment", "charge" },
        new[] { "folder", "directory", "path" },
        new[] { "start", "launch", "begin", "init", "initialize", "startup", "boot" },
        new[] { "stop", "cancel", "abort", "halt", "terminate", "kill" },
        new[] { "message", "chat", "conversation", "prompt", "reply" },
        new[] { "model", "llm", "ollama", "ai", "assistant" },
        new[] { "timeout", "expire", "deadline", "slow", "hang" },
    };

    private static readonly Dictionary<string, List<string>> Synonyms = BuildSynonymMap();

    private static Dictionary<string, List<string>> BuildSynonymMap()
    {
        var map = new Dictionary<string, List<string>>();
        foreach (var group in SynonymGroups)
        {
            var stems = group.Select(Stem).Distinct().ToList();
            foreach (var s in stems)
            {
                if (!map.TryGetValue(s, out var list)) map[s] = list = new List<string>();
                foreach (var o in stems) if (o != s && !list.Contains(o)) list.Add(o);
            }
        }
        return map;
    }

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9]*")]
    private static partial Regex Word();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelBoundary();

    /// <summary>Light suffix stripping so "removing", "removed" and "removes" meet at "remov".</summary>
    internal static string Stem(string w)
    {
        w = w.ToLowerInvariant();
        foreach (var suf in new[] { "ing", "ers", "er", "ed", "es", "s", "e" })
            if (w.Length >= suf.Length + 3 && w.EndsWith(suf, StringComparison.Ordinal)) return w[..^suf.Length];
        return w;
    }

    /// <summary>Splits identifiers (camelCase, snake_case) into lower-case stems; also keeps the whole identifier.</summary>
    internal static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match m in Word().Matches(text))
        {
            var word = m.Value;
            if (word.Length < 3 && !word.All(char.IsUpper)) continue;
            var parts = CamelBoundary().Split(word).Where(p => p.Length >= 2).Select(p => p.ToLowerInvariant()).ToList();
            if (parts.Count > 1 && word.Length >= 4) yield return Stem(word);
            foreach (var p in parts)
            {
                if (p.Length < 3 || Stop.Contains(p)) continue;
                yield return Stem(p);
            }
        }
    }

    public static ProjectIndex Build(ProjectSession s, CancellationToken ct)
    {
        var index = new ProjectIndex();
        var clock = Stopwatch.StartNew();
        var files = s.Files
            .Where(f => f.Size is > 0 and <= MaxFileBytes && !ProjectFilePolicy.IsBinaryExtension(Path.GetExtension(f.RelativePath)))
            .Take(MaxFiles + 1).ToList();
        if (files.Count > MaxFiles) { index.Complete = false; files.RemoveAt(files.Count - 1); }

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (clock.ElapsedMilliseconds > BuildBudgetMs) { index.Complete = false; break; }
            var lines = s.ReadAllLinesOrNull(f.RelativePath, MaxFileBytes);
            if (lines is null) continue;

            var terms = new Dictionary<string, int>();
            var length = 0;
            foreach (var line in lines)
                foreach (var t in Tokenize(line)) { terms[t] = terms.GetValueOrDefault(t) + 1; length++; }
            foreach (var t in Tokenize(f.RelativePath.Replace('\\', ' ').Replace('/', ' ').Replace('.', ' ').Replace('_', ' ')))
                terms[t] = terms.GetValueOrDefault(t) + 3; // path words count extra: they name the concept
            if (terms.Count == 0) continue;
            index._docs.Add(new Doc(f.RelativePath, terms, Math.Max(length, 1)));
        }

        foreach (var d in index._docs)
            foreach (var t in d.Terms.Keys) index._docFreq[t] = index._docFreq.GetValueOrDefault(t) + 1;
        index._avgLen = index._docs.Count == 0 ? 1 : index._docs.Average(d => (double)d.Length);
        return index;
    }

    /// <summary>Query terms (stems) with weights: 1.0 for words the user typed, 0.4 for synonym expansions.</summary>
    public static IReadOnlyDictionary<string, (double Weight, string? Via)> ExpandQuery(string query)
    {
        var terms = new Dictionary<string, (double, string?)>();
        var direct = Tokenize(query).Distinct().Take(16).ToList();
        foreach (var t in direct) terms[t] = (1.0, null);
        foreach (var t in direct)
            if (Synonyms.TryGetValue(t, out var syn))
                foreach (var s in syn.Take(8))
                    if (!terms.ContainsKey(s)) terms[s] = (0.4, t);
        return terms;
    }

    public IReadOnlyList<(string Path, double Score)> Search(IReadOnlyDictionary<string, (double Weight, string? Via)> terms, int take)
    {
        const double k1 = 1.4, b = 0.75;
        var n = _docs.Count;
        var results = new List<(string, double)>();
        foreach (var d in _docs)
        {
            double score = 0;
            foreach (var (term, (weight, _)) in terms)
            {
                if (!d.Terms.TryGetValue(term, out var tf)) continue;
                var df = _docFreq.GetValueOrDefault(term, 1);
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                score += weight * idf * (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * d.Length / _avgLen));
            }
            if (score > 0) results.Add((d.Path, score));
        }
        return results.OrderByDescending(r => r.Item2).ThenBy(r => r.Item1, StringComparer.OrdinalIgnoreCase).Take(take).ToList();
    }
}
