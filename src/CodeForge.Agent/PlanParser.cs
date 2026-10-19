using System.Text;
using System.Text.RegularExpressions;

namespace CodeForge.Agent;

public enum PlanStepStatus { Pending, Running, Done, Skipped }

public sealed record ParsedPlan(string Title, IReadOnlyList<string> Steps);

/// <summary>Extracts a step list from a model reply and builds the prompt for executing one step.</summary>
public static partial class PlanParser
{
    public const int MaxSteps = 12;
    public const int MaxStepChars = 300;

    [GeneratedRegex(@"^[\s>#*`_]*PLAN\b[\s*`_]*[:\-–—]?[\s*`_]*(?<t>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Header();

    [GeneratedRegex(@"^(?<indent>\s*)(?:\d+[.)]|[-*•])\s+(?<t>\S.*)$")]
    private static partial Regex Item();

    [GeneratedRegex(@"^\s*\d+[.)]\s+\S")]
    private static partial Regex NumberedItem();

    public static ParsedPlan? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var start = -1;
        var title = "Plan";
        for (var i = 0; i < lines.Length; i++)
        {
            var h = Header().Match(lines[i]);
            if (!h.Success) continue;
            start = i + 1;
            var t = Clean(h.Groups["t"].Value);
            if (t.Length > 0 && !Item().IsMatch(lines[i])) title = Trunc(t, 80);
            break;
        }

        var steps = start >= 0 ? Collect(lines, start) : new List<string>();
        if (steps.Count == 0)
        {
            // No header: accept the first run of at least two numbered items.
            for (var i = 0; i + 1 < lines.Length && steps.Count == 0; i++)
                if (NumberedItem().IsMatch(lines[i]) && NumberedItem().IsMatch(lines[i + 1]))
                    steps = Collect(lines, i);
            if (steps.Count < 2) steps = new List<string>();
        }
        return steps.Count == 0 ? null : new ParsedPlan(title, steps);
    }

    private static List<string> Collect(string[] lines, int from)
    {
        var steps = new List<string>();
        for (var i = from; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith("```", StringComparison.Ordinal)) continue;
            var m = Item().Match(line);
            if (!m.Success)
            {
                if (steps.Count > 0) break;
                continue; // description before the list
            }
            var text = Clean(m.Groups["t"].Value);
            if (m.Groups["indent"].Value.Length >= 2 && steps.Count > 0)
                steps[^1] = Trunc(steps[^1] + "; " + text, MaxStepChars);
            else if (text.Length > 0)
            {
                if (steps.Count >= MaxSteps) break;
                steps.Add(Trunc(text, MaxStepChars));
            }
        }
        return steps;
    }

    private static string Clean(string s) => s.Replace("**", "").Replace("`", "").Trim().Trim('_').Trim();

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public static string StepPrompt(string title, IReadOnlyList<(string Text, PlanStepStatus Status)> steps, int index)
    {
        var sb = new StringBuilder();
        sb.Append($"Carry out step {index + 1} of {steps.Count} of the plan \"{title}\". Do only this step.\n\nPlan:\n");
        for (var i = 0; i < steps.Count; i++)
        {
            var tag = i == index ? "current" : steps[i].Status switch
            {
                PlanStepStatus.Done => "done", PlanStepStatus.Skipped => "skipped", _ => "pending"
            };
            sb.Append($"{i + 1}. [{tag}] {steps[i].Text}\n");
        }
        sb.Append($"\nStep {index + 1}: {steps[index].Text}\nPropose the code changes for this step using the edit format.");
        return sb.ToString();
    }
}
