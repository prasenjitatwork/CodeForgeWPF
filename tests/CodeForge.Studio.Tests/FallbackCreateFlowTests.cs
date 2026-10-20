namespace CodeForge.Studio.Tests;

public class FallbackCreateFlowTests
{
    [Fact]
    public async Task Reply_with_file_headers_and_fenced_code_creates_proposals_that_apply_after_permission()
    {
        using var h = new Harness(seedProject: false);
        await h.Main.InitializeAsync();
        await h.ProjectVm.OpenAsync(h.Project.Root, persist: false);
        h.Client.Reply = "FILE: index.html\n```html\n<!DOCTYPE html>\n<title>Photo</title>\n```\nFILE: css/style.css\n```css\nbody{margin:0}\n```\n";
        h.Ui.AllowWrite = true;

        await h.SendAsync("create each file in project folder");

        var msg = h.Main.Messages.Last();
        Assert.Equal(2, msg.Changes.Count);
        Assert.All(msg.Changes, c => Assert.Equal(ChangeState.Applied, c.State));
        Assert.True(File.Exists(Path.Combine(h.Project.Root, "index.html")));
        Assert.True(File.Exists(Path.Combine(h.Project.Root, "css", "style.css")));
    }

    [Fact]
    public async Task System_prompt_tells_the_model_it_can_create_files_when_a_project_is_open()
    {
        using var h = new Harness();
        await h.Main.InitializeAsync();
        await h.ProjectVm.OpenAsync(h.Project.Root, persist: false);
        await h.SendAsync("create a file");
        var system = h.Client.Requests.Last().First(m => m.Role == "system").Content;
        Assert.Contains("You CAN create and modify files", system);
    }
}
