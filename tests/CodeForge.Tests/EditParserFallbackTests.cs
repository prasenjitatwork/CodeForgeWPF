using CodeForge.Tools;

namespace CodeForge.Tests;

public class EditParserFallbackTests
{
    [Fact]
    public void File_header_followed_by_fenced_code_becomes_a_new_file_proposal()
    {
        var (blocks, problems) = EditParser.Parse("FILE: index.html\n```html\n<!DOCTYPE html>\n<html></html>\n```\n\nFILE: css/style.css\n```css\nbody { margin: 0; }\n```\n");
        Assert.Empty(problems);
        Assert.Equal(2, blocks.Count);
        Assert.Equal("index.html", blocks[0].Path);
        Assert.Equal("", blocks[0].Search);
        Assert.Equal("<!DOCTYPE html>\n<html></html>", blocks[0].Replace);
        Assert.Equal("css/style.css", blocks[1].Path);
    }

    [Fact]
    public void Loose_marker_variant_is_accepted()
    {
        var (blocks, _) = EditParser.Parse("FILE: gallery.html\n<<>>\n<h1>Gallery</h1>\n<<>>\n");
        Assert.Single(blocks);
        Assert.Equal("gallery.html", blocks[0].Path);
        Assert.Equal("<h1>Gallery</h1>", blocks[0].Replace);
    }

    [Fact]
    public void Unclosed_fence_is_reported_not_guessed()
    {
        var (blocks, problems) = EditParser.Parse("FILE: a.html\n```html\n<p>cut off");
        Assert.Empty(blocks);
        Assert.Contains(problems, p => p.Contains("a.html"));
    }

    [Fact]
    public void Proper_blocks_are_not_duplicated_by_the_fallback()
    {
        var (blocks, _) = EditParser.Parse("FILE: a.cs\n<<<<<<< SEARCH\n=======\nclass A {}\n>>>>>>> REPLACE\n");
        Assert.Single(blocks);
    }

    [Fact]
    public void Plain_explanations_and_unrelated_code_fences_create_nothing()
    {
        var (blocks, _) = EditParser.Parse("Here is an example:\n```html\n<p>x</p>\n```\n");
        Assert.Empty(blocks);
    }

    [Fact]
    public async Task Fallback_proposal_for_an_existing_file_cannot_overwrite_it()
    {
        using var p = new TempProject();
        var ws = new ProjectWorkspace();
        await ws.OpenAsync(p.Root);
        var edits = new EditService(ws, Path.Combine(p.Root, "..", "bk_" + Guid.NewGuid().ToString("N")));
        var r = await edits.PrepareAsync("FILE: README.md\n```\nreplaced\n```\n");
        Assert.True(r.Changes.Count == 0 || r.Problems.Count > 0);
        Assert.Contains("Demo", File.ReadAllText(Path.Combine(p.Root, "README.md")));
    }
}
