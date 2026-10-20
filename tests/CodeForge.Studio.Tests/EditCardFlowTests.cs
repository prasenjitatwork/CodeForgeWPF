namespace CodeForge.Studio.Tests;

public class EditCardFlowTests
{
    private static string Block(string path, string search, string replace) =>
        $"FILE: {path}\n<<<<<<< SEARCH\n{search}\n=======\n{replace}\n>>>>>>> REPLACE\n";

    private static async Task<Harness> OpenAsync(string reply)
    {
        var h = new Harness();
        await h.Main.InitializeAsync();
        await h.ProjectVm.OpenAsync(h.Project.Root, persist: false);
        h.Client.Reply = reply;
        return h;
    }

    private string P(Harness h, string rel) => Path.Combine(h.Project.Root, rel.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Edit_waits_for_folder_permission_and_stays_pending_when_denied()
    {
        using var h = await OpenAsync(Block("src/Unrelated.cs", "Banana", "Apple"));
        h.Ui.AllowWrite = false;

        await h.SendAsync("rename the class");

        var card = h.Main.Messages.Last().Changes.Single();
        Assert.Equal(1, h.Ui.WritePrompts);
        Assert.Equal(ChangeState.Pending, card.State);
        Assert.Contains("Banana", File.ReadAllText(P(h, "src/Unrelated.cs")));
    }

    [Fact]
    public async Task Edit_auto_applies_after_permission_and_undo_restores_the_file()
    {
        using var h = await OpenAsync(Block("src/Unrelated.cs", "Banana", "Apple"));
        h.Ui.AllowWrite = true;

        await h.SendAsync("rename the class");

        var card = h.Main.Messages.Last().Changes.Single();
        Assert.Equal(ChangeState.Applied, card.State);
        Assert.Contains("Apple", File.ReadAllText(P(h, "src/Unrelated.cs")));

        await card.UndoCommand.ExecuteAsync(null);
        Assert.Equal(ChangeState.Undone, card.State);
        Assert.Contains("Banana", File.ReadAllText(P(h, "src/Unrelated.cs")));
    }

    [Fact]
    public async Task Permission_is_asked_only_once_per_folder()
    {
        using var h = await OpenAsync(Block("src/Unrelated.cs", "Banana", "Apple"));
        h.Ui.AllowWrite = true;
        await h.SendAsync("one");
        h.Client.Reply = Block("src/Unrelated.cs", "Apple", "Cherry");
        await h.SendAsync("two");
        Assert.Equal(1, h.Ui.WritePrompts);
    }

    [Fact]
    public async Task Rejecting_a_proposal_leaves_the_file_untouched_and_blocks_apply()
    {
        using var h = await OpenAsync(Block("src/Unrelated.cs", "Banana", "Apple"));
        h.Ui.AllowWrite = false;
        await h.SendAsync("rename");
        var card = h.Main.Messages.Last().Changes.Single();

        await card.RejectCommand.ExecuteAsync(null);

        Assert.Equal(ChangeState.Rejected, card.State);
        Assert.False(card.CanApply);
        await card.ApplyCommand.ExecuteAsync(null);
        Assert.NotEqual(ChangeState.Applied, card.State);
        Assert.Contains("Banana", File.ReadAllText(P(h, "src/Unrelated.cs")));
    }

    [Fact]
    public async Task Delete_proposal_never_auto_applies_even_with_folder_permission()
    {
        using var h = await OpenAsync("DELETE: src/Unrelated.cs");
        h.Ui.AllowWrite = true;

        await h.SendAsync("remove the unused file");

        var card = h.Main.Messages.Last().Changes.Single();
        Assert.True(card.RequiresManualApproval);
        Assert.Equal(ChangeState.Pending, card.State);
        Assert.Equal(0, h.Ui.WritePrompts);
        Assert.True(File.Exists(P(h, "src/Unrelated.cs")));

        await card.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(ChangeState.Applied, card.State);
        Assert.False(File.Exists(P(h, "src/Unrelated.cs")));

        await card.UndoCommand.ExecuteAsync(null);
        Assert.True(File.Exists(P(h, "src/Unrelated.cs")));
    }

    [Fact]
    public async Task Rename_proposal_needs_a_click_and_can_be_undone()
    {
        using var h = await OpenAsync("RENAME: src/Unrelated.cs -> src/Renamed.cs");
        h.Ui.AllowWrite = true;

        await h.SendAsync("rename the file");

        var card = h.Main.Messages.Last().Changes.Single();
        Assert.True(card.RequiresManualApproval);
        Assert.Equal(ChangeState.Pending, card.State);

        await card.ApplyCommand.ExecuteAsync(null);
        Assert.True(File.Exists(P(h, "src/Renamed.cs")));
        Assert.False(File.Exists(P(h, "src/Unrelated.cs")));

        await card.UndoCommand.ExecuteAsync(null);
        Assert.True(File.Exists(P(h, "src/Unrelated.cs")));
        Assert.False(File.Exists(P(h, "src/Renamed.cs")));
    }

    [Fact]
    public async Task Proposals_for_secret_or_outside_paths_produce_no_card()
    {
        using var h = await OpenAsync("DELETE: .env\nDELETE: ../outside.txt");
        await h.SendAsync("clean up");
        var msg = h.Main.Messages.Last();
        Assert.Empty(msg.Changes);
        Assert.True(msg.HasProblems);
        Assert.True(File.Exists(P(h, ".env")));
    }

    [Fact]
    public async Task Stale_file_blocks_apply_and_leaves_the_user_edit_intact()
    {
        using var h = await OpenAsync(Block("src/Unrelated.cs", "Banana", "Apple"));
        h.Ui.AllowWrite = false;
        await h.SendAsync("rename");
        var card = h.Main.Messages.Last().Changes.Single();

        File.WriteAllText(P(h, "src/Unrelated.cs"), "namespace Core;\npublic class Banana { int edited; }\n");
        await card.ApplyCommand.ExecuteAsync(null);

        Assert.NotEqual(ChangeState.Applied, card.State);
        Assert.Contains("edited", File.ReadAllText(P(h, "src/Unrelated.cs")));
    }

    [Fact]
    public async Task Pending_proposal_is_restored_after_restart_and_needs_a_click()
    {
        using var first = await OpenAsync(Block("src/Unrelated.cs", "Banana", "Apple"));
        first.Ui.AllowWrite = false;
        await first.SendAsync("rename");
        first.Main.Messages.Last().Changes.Single();

        using var second = new Harness(restartOf: first);
        await second.Main.InitializeAsync();
        await second.ProjectVm.OpenAsync(second.Project.Root, persist: false);
        second.Ui.AllowWrite = true;
        second.Main.SelectedConversation = second.Main.Conversations.First();

        Assert.True(await Harness.WaitFor(() => second.Main.Messages.Any(m => m.Changes.Count > 0)));
        var card = second.Main.Messages.First(m => m.Changes.Count > 0).Changes.Single();
        Assert.Equal(ChangeState.Pending, card.State);
        Assert.Contains("Banana", File.ReadAllText(P(second, "src/Unrelated.cs")));

        await card.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(ChangeState.Applied, card.State);
        Assert.Contains("Apple", File.ReadAllText(P(second, "src/Unrelated.cs")));
    }

    [Fact]
    public async Task Restored_proposal_is_marked_stale_when_the_file_changed_while_closed()
    {
        using var first = await OpenAsync(Block("src/Unrelated.cs", "Banana", "Apple"));
        first.Ui.AllowWrite = false;
        await first.SendAsync("rename");
        File.WriteAllText(P(first, "src/Unrelated.cs"), "namespace Core;\npublic class Banana { int changed; }\n");

        using var second = new Harness(restartOf: first);
        await second.Main.InitializeAsync();
        await second.ProjectVm.OpenAsync(second.Project.Root, persist: false);
        second.Main.SelectedConversation = second.Main.Conversations.First();

        Assert.True(await Harness.WaitFor(() => second.Main.Messages.Any(m => m.Changes.Count > 0)));
        var card = second.Main.Messages.First(m => m.Changes.Count > 0).Changes.Single();
        Assert.Equal(ChangeState.Stale, card.State);
        Assert.False(card.CanApply);
        await card.ApplyCommand.ExecuteAsync(null);
        Assert.NotEqual(ChangeState.Applied, card.State);
        Assert.Contains("changed", File.ReadAllText(P(second, "src/Unrelated.cs")));
    }
}
