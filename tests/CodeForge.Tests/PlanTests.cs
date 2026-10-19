using CodeForge.Agent;
using Xunit;

namespace CodeForge.Tests;

public class PlanTests
{
    [Fact]
    public void Parse_HeaderAndNumberedSteps()
    {
        var p = PlanParser.Parse("Sure.\n\nPLAN: Add logging\n1. Create Logger class\n2. Use it in Program\n3. Add a test\n\nDone.");
        Assert.NotNull(p);
        Assert.Equal("Add logging", p!.Title);
        Assert.Equal(3, p.Steps.Count);
        Assert.Equal("Create Logger class", p.Steps[0]);
    }

    [Fact]
    public void Parse_MarkdownHeaderAndBullets()
    {
        var p = PlanParser.Parse("**PLAN: Refactor**\n- Split `Foo`\n- **Rename** Bar");
        Assert.NotNull(p);
        Assert.Equal("Refactor", p!.Title);
        Assert.Equal(new[] { "Split Foo", "Rename Bar" }, p.Steps);
    }

    [Fact]
    public void Parse_SubItemsJoinPreviousStep()
    {
        var p = PlanParser.Parse("PLAN: X\n1. Do A\n   - detail one\n2. Do B");
        Assert.NotNull(p);
        Assert.Equal(2, p!.Steps.Count);
        Assert.Contains("detail one", p.Steps[0]);
    }

    [Fact]
    public void Parse_FallbackWithoutHeader()
    {
        var p = PlanParser.Parse("Here is what to do:\n1. First\n2. Second");
        Assert.NotNull(p);
        Assert.Equal("Plan", p!.Title);
        Assert.Equal(2, p.Steps.Count);
    }

    [Fact]
    public void Parse_SingleItemNoHeader_ReturnsNull() =>
        Assert.Null(PlanParser.Parse("1. Only one"));

    [Fact]
    public void Parse_PlainText_ReturnsNull() =>
        Assert.Null(PlanParser.Parse("I would just change the file."));

    [Fact]
    public void Parse_CapsStepsAndLength()
    {
        var text = "PLAN: Big\n" + string.Join("\n", Enumerable.Range(1, 20).Select(i => $"{i}. " + new string('x', 500)));
        var p = PlanParser.Parse(text);
        Assert.NotNull(p);
        Assert.Equal(PlanParser.MaxSteps, p!.Steps.Count);
        Assert.All(p.Steps, s => Assert.True(s.Length <= PlanParser.MaxStepChars + 3));
    }

    [Fact]
    public void StepPrompt_TagsSteps()
    {
        var steps = new List<(string, PlanStepStatus)>
        {
            ("A", PlanStepStatus.Done), ("B", PlanStepStatus.Pending), ("C", PlanStepStatus.Skipped)
        };
        var s = PlanParser.StepPrompt("T", steps, 1);
        Assert.Contains("step 2 of 3", s);
        Assert.Contains("[done]", s);
        Assert.Contains("[current]", s);
        Assert.Contains("[skipped]", s);
    }

    [Fact]
    public void BuildPrompt_PlanMode_UsesPlanInstructionsOnly()
    {
        var msgs = new[] { new CodeForge.Domain.ChatMessage { Role = CodeForge.Domain.ChatRole.User, Content = "do it" } };
        var plan = ChatAgent.BuildPrompt("sys", msgs, "ctx", planMode: true);
        var normal = ChatAgent.BuildPrompt("sys", msgs, "ctx");
        Assert.Contains("PLAN:", plan[0].Content);
        Assert.DoesNotContain("SEARCH", plan[0].Content);
        Assert.Contains("SEARCH", normal[0].Content);
    }
}
