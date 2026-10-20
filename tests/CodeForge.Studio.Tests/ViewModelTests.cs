using CodeForge.Application;
using CodeForge.Studio.Services;
using CodeForge.Studio.ViewModels;

namespace CodeForge.Studio.Tests;

public class ProjectViewModelTests
{
    [Fact]
    public async Task Open_sets_project_info_tree_and_remembers_path()
    {
        using var h = new Harness();
        await h.ProjectVm.OpenAsync(h.Project.Root, persist: true);

        Assert.True(h.ProjectVm.HasProject);
        Assert.Null(h.ProjectVm.Error);
        Assert.NotEmpty(h.ProjectVm.Roots);
        Assert.Equal(h.Project.Root, h.Settings.Current.LastProjectPath);
    }

    [Fact]
    public async Task Open_folder_command_uses_the_picker()
    {
        using var h = new Harness();
        h.Ui.FolderToPick = h.Project.Root;
        await h.ProjectVm.OpenFolderCommand.ExecuteAsync(null);
        Assert.True(h.ProjectVm.HasProject);
    }

    [Fact]
    public async Task Cancelled_picker_leaves_state_unchanged()
    {
        using var h = new Harness();
        h.Ui.FolderToPick = null;
        await h.ProjectVm.OpenFolderCommand.ExecuteAsync(null);
        Assert.False(h.ProjectVm.HasProject);
    }

    [Fact]
    public async Task Opening_a_missing_folder_reports_an_error()
    {
        using var h = new Harness();
        await h.ProjectVm.OpenAsync(Path.Combine(h.Project.Root, "does-not-exist"), persist: true);
        Assert.False(h.ProjectVm.HasProject);
        Assert.False(string.IsNullOrEmpty(h.ProjectVm.Error));
    }

    [Fact]
    public async Task Close_clears_everything_and_forgets_the_path()
    {
        using var h = new Harness();
        await h.ProjectVm.OpenAsync(h.Project.Root, persist: true);
        h.ProjectVm.SearchText = "Invoice";
        await Harness.WaitFor(() => h.ProjectVm.HasResults);

        await h.ProjectVm.CloseProjectCommand.ExecuteAsync(null);

        Assert.False(h.ProjectVm.HasProject);
        Assert.Empty(h.ProjectVm.Roots);
        Assert.Empty(h.ProjectVm.Results);
        Assert.Equal(string.Empty, h.ProjectVm.SearchText);
        Assert.Null(h.Settings.Current.LastProjectPath);
    }

    [Fact]
    public async Task Restore_last_project_reopens_the_saved_folder()
    {
        using var h = new Harness();
        var s = h.Settings.Current.Clone();
        s.LastProjectPath = h.Project.Root;
        await h.Settings.SaveAsync(s);

        await h.ProjectVm.RestoreLastProjectAsync();
        Assert.True(h.ProjectVm.HasProject);
    }

    [Fact]
    public async Task Selecting_a_file_node_previews_it_and_directories_do_not()
    {
        using var h = new Harness();
        await h.ProjectVm.OpenAsync(h.Project.Root, persist: false);
        var readme = h.ProjectVm.Roots.First(n => n.Name == "README.md");
        await h.ProjectVm.SelectNodeAsync(readme);
        Assert.Equal("README.md", h.ProjectVm.PreviewTitle);
        Assert.Contains("Demo", h.ProjectVm.PreviewText);

        var dir = h.ProjectVm.Roots.First(n => n.IsDirectory);
        await h.ProjectVm.SelectNodeAsync(dir);
        Assert.Equal("README.md", h.ProjectVm.PreviewTitle);
    }

    [Fact]
    public async Task Preview_of_a_secret_file_never_shows_its_content()
    {
        using var h = new Harness();
        await h.ProjectVm.OpenAsync(h.Project.Root, persist: false);
        await h.ProjectVm.PreviewAsync(".env");
        Assert.DoesNotContain("super-secret", h.ProjectVm.PreviewText);
    }

    [Fact]
    public async Task Search_finds_matches_and_sets_the_header()
    {
        using var h = new Harness();
        await h.ProjectVm.OpenAsync(h.Project.Root, persist: false);

        h.ProjectVm.SearchText = "InvoiceCalculator";
        Assert.True(await Harness.WaitFor(() => h.ProjectVm.HasResults));

        Assert.Contains(h.ProjectVm.Results, r => r.RelativePath.EndsWith("InvoiceCalculator.cs"));
        Assert.Contains("match", h.ProjectVm.ResultsHeader);
    }

    [Fact]
    public async Task Search_never_returns_secret_file_content()
    {
        using var h = new Harness();
        await h.ProjectVm.OpenAsync(h.Project.Root, persist: false);
        h.ProjectVm.SearchText = "super-secret";
        await Task.Delay(700);
        Assert.DoesNotContain(h.ProjectVm.Results, r => r.RelativePath.Contains(".env"));
    }

    [Fact]
    public async Task Very_short_query_clears_results_and_clear_command_resets_text()
    {
        using var h = new Harness();
        await h.ProjectVm.OpenAsync(h.Project.Root, persist: false);
        h.ProjectVm.SearchText = "Invoice";
        Assert.True(await Harness.WaitFor(() => h.ProjectVm.HasResults));

        h.ProjectVm.SearchText = "I";
        Assert.True(await Harness.WaitFor(() => !h.ProjectVm.HasResults));
        Assert.Empty(h.ProjectVm.Results);

        h.ProjectVm.SearchText = "Invoice";
        h.ProjectVm.ClearSearchCommand.Execute(null);
        Assert.Equal(string.Empty, h.ProjectVm.SearchText);
    }

    [Fact]
    public async Task Search_without_a_project_yields_nothing()
    {
        using var h = new Harness();
        h.ProjectVm.SearchText = "anything";
        await Task.Delay(500);
        Assert.False(h.ProjectVm.HasResults);
    }

    [Fact]
    public async Task Explain_project_requests_a_prompt_only_when_a_project_is_open()
    {
        using var h = new Harness();
        string? prompt = null;
        h.ProjectVm.PromptRequested += (_, p) => prompt = p;

        h.ProjectVm.ExplainProjectCommand.Execute(null);
        Assert.Null(prompt);

        await h.ProjectVm.OpenAsync(h.Project.Root, persist: false);
        h.ProjectVm.ExplainProjectCommand.Execute(null);
        Assert.Contains("Explain this project", prompt);
    }

    [Fact]
    public async Task Memory_notes_are_saved_per_project()
    {
        using var h = new Harness();
        await h.ProjectVm.OpenAsync(h.Project.Root, persist: false);
        var err = await h.ProjectVm.RememberAsync("Use xUnit for tests");
        Assert.Null(err);
        Assert.Contains(h.ProjectVm.Notes, n => n.Text == "Use xUnit for tests");
    }
}

public class PlanViewModelTests
{
    private static PlanViewModel Make() =>
        new(PlanParser.Parse("PLAN: Add logging\n1. Create Logger\n2. Use it\n3. Test it")!);

    [Fact]
    public void Starts_with_all_steps_pending_and_zero_progress()
    {
        var p = Make();
        Assert.Equal(3, p.Steps.Count);
        Assert.All(p.Steps, s => Assert.Equal(PlanStepStatus.Pending, s.Status));
        Assert.Equal("0/3 done", p.Progress);
    }

    [Fact]
    public void Mark_done_and_skip_update_progress_and_raise_changed()
    {
        var p = Make();
        var changes = 0;
        p.Changed += (_, _) => changes++;

        p.MarkDoneCommand.Execute(p.Steps[0]);
        p.SkipCommand.Execute(p.Steps[1]);

        Assert.Equal("1/3 done, 1 skipped", p.Progress);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Done_steps_cannot_be_changed_again()
    {
        var p = Make();
        p.MarkDoneCommand.Execute(p.Steps[0]);
        p.SkipCommand.Execute(p.Steps[0]);
        Assert.Equal(PlanStepStatus.Done, p.Steps[0].Status);
        Assert.False(p.Steps[0].CanRun);
    }

    [Fact]
    public void Run_step_requests_that_step_but_not_a_finished_one()
    {
        var p = Make();
        var requested = new List<int>();
        p.StepRequested += (_, e) => requested.Add(e.Step.Number);

        p.RunStepCommand.Execute(p.Steps[1]);
        p.MarkDoneCommand.Execute(p.Steps[2]);
        p.RunStepCommand.Execute(p.Steps[2]);

        Assert.Equal(new[] { 2 }, requested);
    }

    [Fact]
    public void Run_next_picks_running_step_first_then_first_pending()
    {
        var p = Make();
        var requested = new List<int>();
        p.StepRequested += (_, e) => requested.Add(e.Step.Number);

        p.RunNextCommand.Execute(null);
        p.Steps[0].Status = PlanStepStatus.Done;
        p.Steps[2].Status = PlanStepStatus.Running;
        p.RunNextCommand.Execute(null);
        p.Steps[2].Status = PlanStepStatus.Done;
        p.RunNextCommand.Execute(null);

        Assert.Equal(new[] { 1, 3, 2 }, requested);
    }

    [Fact]
    public void Run_next_does_nothing_when_everything_is_finished()
    {
        var p = Make();
        foreach (var s in p.Steps) s.Status = PlanStepStatus.Done;
        var fired = false;
        p.StepRequested += (_, _) => fired = true;
        p.RunNextCommand.Execute(null);
        Assert.False(fired);
    }

    [Fact]
    public void Stored_plan_round_trips_and_a_running_step_returns_to_pending()
    {
        var p = Make();
        p.Steps[0].Status = PlanStepStatus.Done;
        p.Steps[1].Status = PlanStepStatus.Running;

        var restored = new PlanViewModel(p.ToStored(Guid.NewGuid()));

        Assert.Equal(PlanStepStatus.Done, restored.Steps[0].Status);
        Assert.Equal(PlanStepStatus.Pending, restored.Steps[1].Status);
        Assert.Equal(p.Title, restored.Title);
    }

    [Fact]
    public void Step_prompt_mentions_the_step_text()
    {
        var p = Make();
        Assert.Contains("Use it", p.BuildStepPrompt(p.Steps[1]));
    }
}

public class MainViewModelFlowTests
{
    private const string TwoStepPlan = "PLAN: Add logging\n1. Create Logger\n2. Use it in Program";

    [Fact]
    public async Task Plan_mode_reply_attaches_a_plan_and_turns_plan_mode_off()
    {
        using var h = new Harness(seedProject: false);
        await h.Main.InitializeAsync();
        h.Client.Reply = TwoStepPlan;

        await h.SendAsync("Plan adding logging", planMode: true);

        var reply = h.Main.Messages.Last();
        Assert.True(reply.HasPlan);
        Assert.Equal(2, reply.Plan!.Steps.Count);
        Assert.False(h.Main.PlanMode);
    }

    [Fact]
    public async Task Plan_mode_reply_without_steps_shows_a_problem_instead()
    {
        using var h = new Harness(seedProject: false);
        await h.Main.InitializeAsync();
        h.Client.Reply = "I would just change the file.";

        await h.SendAsync("plan it", planMode: true);

        var reply = h.Main.Messages.Last();
        Assert.False(reply.HasPlan);
        Assert.True(reply.HasProblems);
    }

    [Fact]
    public async Task Running_a_plan_step_sends_a_step_prompt_and_marks_it_running()
    {
        using var h = new Harness(seedProject: false);
        await h.Main.InitializeAsync();
        h.Client.Reply = TwoStepPlan;
        await h.SendAsync("Plan adding logging", planMode: true);
        var plan = h.Main.Messages.Last().Plan!;
        h.Client.Reply = "Here is how to create the logger.";

        plan.RunStepCommand.Execute(plan.Steps[0]);
        Assert.True(await Harness.WaitFor(() => !h.Main.IsGenerating && h.Client.Requests.Count == 2));

        Assert.Equal(PlanStepStatus.Running, plan.Steps[0].Status);
        var lastUser = h.Client.Requests[1].Last(m => m.Role == "user").Content;
        Assert.Contains("Create Logger", lastUser);
    }

    [Fact]
    public async Task Remember_command_saves_a_note_and_is_not_sent_to_the_model()
    {
        using var h = new Harness();
        await h.Main.InitializeAsync();
        await h.ProjectVm.OpenAsync(h.Project.Root, persist: false);

        await h.SendAsync("/remember we use tabs");

        Assert.Contains(h.ProjectVm.Notes, n => n.Text == "we use tabs");
        Assert.Empty(h.Client.Requests);
        Assert.Contains("Saved to project memory", h.Main.Messages.Last().Content);
    }

    [Fact]
    public async Task Remember_command_without_a_project_explains_why_it_was_not_saved()
    {
        using var h = new Harness(seedProject: false);
        await h.Main.InitializeAsync();
        await h.SendAsync("/remember something");
        Assert.Contains("Not saved", h.Main.Messages.Last().Content);
    }

    [Fact]
    public async Task Send_is_disabled_for_blank_input()
    {
        using var h = new Harness(seedProject: false);
        await h.Main.InitializeAsync();
        h.Main.InputText = "   ";
        Assert.False(h.Main.SendCommand.CanExecute(null));
        h.Main.InputText = "hello";
        Assert.True(h.Main.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task Project_reply_exposes_the_context_inspector_text()
    {
        using var h = new Harness();
        await h.Main.InitializeAsync();
        await h.ProjectVm.OpenAsync(h.Project.Root, persist: false);

        await h.SendAsync("how does the InvoiceCalculator compute totals?");

        var reply = h.Main.Messages.Last();
        Assert.True(reply.HasContextDetails);
        Assert.Contains("InvoiceCalculator.cs", reply.ContextDetails);
        Assert.DoesNotContain("super-secret", reply.ContextDetails);
    }
}

public class ConnectionAndOnboardingTests
{
    [Fact]
    public async Task First_run_shows_the_guide_when_no_model_is_installed()
    {
        using var h = new Harness(seedProject: false);
        h.Client.Models = new();
        await h.Main.InitializeAsync();
        Assert.Equal(1, h.Ui.OnboardingShown);
    }

    [Fact]
    public async Task First_run_shows_the_guide_when_ollama_is_unreachable()
    {
        using var h = new Harness(seedProject: false);
        h.Client.ListError = new ModelException(ModelErrorKind.Unavailable, "Cannot reach Ollama at x");
        await h.Main.InitializeAsync();
        Assert.Equal(1, h.Ui.OnboardingShown);
        Assert.Equal(ConnectionState.Disconnected, h.Main.ConnectionState);
        Assert.Contains("Setup guide", h.Main.ConnectionText);
    }

    [Fact]
    public async Task Healthy_setup_skips_the_guide_and_marks_it_completed()
    {
        using var h = new Harness(seedProject: false);
        await h.Main.InitializeAsync();
        Assert.Equal(0, h.Ui.OnboardingShown);
        Assert.True(h.Settings.Current.OnboardingCompleted);
    }

    [Fact]
    public async Task Guide_is_not_shown_again_once_completed_and_can_be_reopened_manually()
    {
        using var h = new Harness(seedProject: false);
        h.Client.Models = new();
        await h.Main.InitializeAsync();
        var vm = h.Get<OnboardingViewModel>();
        await vm.FinishCommand.ExecuteAsync(null);
        Assert.True(h.Settings.Current.OnboardingCompleted);

        await h.Main.InitializeAsync();
        Assert.Equal(1, h.Ui.OnboardingShown);

        await h.Main.OpenOnboardingCommand.ExecuteAsync(null);
        Assert.Equal(2, h.Ui.OnboardingShown);
    }

    [Fact]
    public async Task Missing_selected_model_is_explained_with_the_pull_command()
    {
        using var h = new Harness(seedProject: false);
        h.Client.Models = new() { "llama3:8b" };
        await h.Main.InitializeAsync();
        Assert.Contains("ollama pull " + AppSettings.DefaultModel, h.Main.ConnectionText);
        Assert.Contains("not installed", h.Main.ConnectionText);
    }

    [Fact]
    public async Task Onboarding_check_reports_each_failure_kind()
    {
        using var h = new Harness(seedProject: false);
        var vm = h.Get<OnboardingViewModel>();

        h.Client.ListError = new ModelException(ModelErrorKind.Unavailable, "down");
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.False(vm.IsOk);
        Assert.Contains("Cannot reach Ollama", vm.StatusText);
        Assert.Contains("ollama.com/download", vm.HintText);

        h.Client.ListError = new ModelException(ModelErrorKind.Timeout, "slow");
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Contains("did not respond in time", vm.StatusText);

        h.Client.ListError = null;
        h.Client.Models = new() { "other:1b" };
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Contains("not installed", vm.StatusText);
        Assert.Contains("ollama pull", vm.HintText);

        h.Client.Models = new() { AppSettings.DefaultModel };
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.IsOk);
    }
}

public class PermissionTextTests
{
    [Fact]
    public void Run_prompt_shows_folder_and_exact_command_and_warns_about_project_code()
    {
        var text = PermissionText.RunCommands(@"C:\Work\MyApp", "dotnet build");
        Assert.Contains(@"C:\Work\MyApp", text);
        Assert.Contains("dotnet build", text);
        Assert.Contains("project itself controls", text);
        Assert.Contains("Choose Yes only if you trust this project", text);
    }

    [Fact]
    public void Run_prompt_lists_only_the_fixed_allowed_commands()
    {
        var text = PermissionText.RunCommands("C:\\x", "dotnet test");
        Assert.Contains("dotnet build, dotnet test, flutter analyze, flutter test", text);
        Assert.DoesNotContain("powershell", text, StringComparison.OrdinalIgnoreCase);
    }
}

public class ContextInspectorTextTests
{
    [Fact]
    public void Overview_only_context_is_described_honestly()
    {
        var text = MessageViewModel.FormatContext(new ProjectContext("t", new List<string>()));
        Assert.Contains("overview", text);
    }

    [Fact]
    public void Excerpts_are_listed_with_file_range_reason_and_text()
    {
        var ctx = new ProjectContext("t", new List<string> { "a.cs" },
            new List<ContextExcerpt> { new("a.cs", "1-3", "matched 'invoice'", "line one") });
        var text = MessageViewModel.FormatContext(ctx);
        Assert.Contains("a.cs", text);
        Assert.Contains("1-3", text);
        Assert.Contains("matched 'invoice'", text);
        Assert.Contains("line one", text);
    }
}
