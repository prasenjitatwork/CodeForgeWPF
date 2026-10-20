using CodeForge.Application;
using CodeForge.Infrastructure.Persistence;
using CodeForge.Tools;
using Xunit;

namespace CodeForge.Tests;

public class EditRecoveryTests : IAsyncLifetime
{
    private readonly TempProject _p = new();
    private readonly string _backup = Path.Combine(Path.GetTempPath(), "cf_bk_" + Guid.NewGuid().ToString("N"));
    private readonly string _db = Path.Combine(Path.GetTempPath(), "cf_ed_" + Guid.NewGuid().ToString("N") + ".db");
    private ProjectWorkspace _w = new();
    private EditService _e = null!;

    public async Task InitializeAsync() { await _w.OpenAsync(_p.Root); _e = new EditService(_w, _backup); }
    public Task DisposeAsync()
    {
        _p.Dispose();
        if (Directory.Exists(_backup)) Directory.Delete(_backup, true);
        try { File.Delete(_db); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private static string Block(string path, string search, string replace) =>
        $"FILE: {path}\n<<<<<<< SEARCH\n{search}\n=======\n{replace}\n>>>>>>> REPLACE\n";

    private string Abs(string rel) => Path.Combine(_p.Root, rel.Replace('/', Path.DirectorySeparatorChar));

    private async Task<FileChange> PrepareOne(string text)
    {
        var r = await _e.PrepareAsync(text);
        return Assert.Single(r.Changes);
    }

    private static FileChange Strip(FileChange c) => c with { Diff = Array.Empty<DiffLine>(), Added = 0, Removed = 0 };

    // ---- delete / rename ----

    [Fact]
    public async Task Delete_ApplyBacksUp_ThenUndoRestores()
    {
        var original = File.ReadAllText(Abs("src/Core/Unrelated.cs"));
        var c = await PrepareOne("DELETE: src/Core/Unrelated.cs");
        Assert.Equal(EditKind.Delete, c.Kind);
        Assert.True(c.Removed > 0);
        Assert.True(File.Exists(Abs("src/Core/Unrelated.cs")));

        await _e.ApplyAsync(c);
        Assert.False(File.Exists(Abs("src/Core/Unrelated.cs")));
        Assert.True(Directory.EnumerateFiles(_backup, "Unrelated.cs", SearchOption.AllDirectories).Any());

        await _e.UndoAsync(c);
        Assert.Equal(original, File.ReadAllText(Abs("src/Core/Unrelated.cs")));
    }

    [Fact]
    public async Task Delete_StaleFile_Refused()
    {
        var c = await PrepareOne("DELETE: src/Core/Unrelated.cs");
        File.AppendAllText(Abs("src/Core/Unrelated.cs"), "// edited elsewhere\n");
        await Assert.ThrowsAsync<ProjectException>(() => _e.ApplyAsync(c));
        Assert.True(File.Exists(Abs("src/Core/Unrelated.cs")));
    }

    [Fact]
    public async Task Delete_UndoRefusedWhenSomethingNowAtPath()
    {
        var c = await PrepareOne("DELETE: src/Core/Unrelated.cs");
        await _e.ApplyAsync(c);
        File.WriteAllText(Abs("src/Core/Unrelated.cs"), "new work");
        await Assert.ThrowsAsync<ProjectException>(() => _e.UndoAsync(c));
        Assert.Equal("new work", File.ReadAllText(Abs("src/Core/Unrelated.cs")));
    }

    [Theory]
    [InlineData("DELETE: .env")]
    [InlineData("DELETE: secrets/server.pem")]
    [InlineData("DELETE: ../outside.txt")]
    [InlineData("DELETE: node_modules/x/index.js")]
    [InlineData("DELETE: image.png")]
    [InlineData("DELETE: missing.cs")]
    [InlineData("DELETE: src")]
    public async Task Delete_ProtectedOrInvalid_IsProblemNotChange(string text)
    {
        var r = await _e.PrepareAsync(text);
        Assert.Empty(r.Changes);
        Assert.NotEmpty(r.Problems);
    }

    [Fact]
    public async Task Rename_ApplyMoves_UndoMovesBack()
    {
        var original = File.ReadAllText(Abs("src/Core/Unrelated.cs"));
        var c = await PrepareOne("RENAME: src/Core/Unrelated.cs -> src/Core/Renamed/Fruit.cs");
        Assert.Equal(EditKind.Rename, c.Kind);
        Assert.Equal("src/Core/Renamed/Fruit.cs", c.NewPath);

        await _e.ApplyAsync(c);
        Assert.False(File.Exists(Abs("src/Core/Unrelated.cs")));
        Assert.Equal(original, File.ReadAllText(Abs("src/Core/Renamed/Fruit.cs")));

        await _e.UndoAsync(c);
        Assert.Equal(original, File.ReadAllText(Abs("src/Core/Unrelated.cs")));
        Assert.False(File.Exists(Abs("src/Core/Renamed/Fruit.cs")));
    }

    [Theory]
    [InlineData("RENAME: src/Core/Unrelated.cs -> src/Core/InvoiceCalculator.cs")]
    [InlineData("RENAME: src/Core/Unrelated.cs -> .env")]
    [InlineData("RENAME: src/Core/Unrelated.cs -> ../escape.cs")]
    [InlineData("RENAME: src/Core/Unrelated.cs -> node_modules/a.cs")]
    [InlineData("RENAME: .env -> env.txt")]
    [InlineData("RENAME: src/Core/Unrelated.cs")]
    public async Task Rename_UnsafeOrMalformed_IsProblemNotChange(string text)
    {
        var r = await _e.PrepareAsync(text);
        Assert.Empty(r.Changes);
        Assert.NotEmpty(r.Problems);
    }

    [Fact]
    public async Task Rename_StaleOrTargetAppears_Refused()
    {
        var c = await PrepareOne("RENAME: src/Core/Unrelated.cs -> src/Core/Fruit.cs");
        File.WriteAllText(Abs("src/Core/Fruit.cs"), "occupied");
        await Assert.ThrowsAsync<ProjectException>(() => _e.ApplyAsync(c));
        Assert.True(File.Exists(Abs("src/Core/Unrelated.cs")));
        Assert.Equal("occupied", File.ReadAllText(Abs("src/Core/Fruit.cs")));
    }

    [Fact]
    public async Task Rename_UndoRefusedIfRenamedFileModified()
    {
        var c = await PrepareOne("RENAME: src/Core/Unrelated.cs -> src/Core/Fruit.cs");
        await _e.ApplyAsync(c);
        File.AppendAllText(Abs("src/Core/Fruit.cs"), "// later work\n");
        await Assert.ThrowsAsync<ProjectException>(() => _e.UndoAsync(c));
        Assert.True(File.Exists(Abs("src/Core/Fruit.cs")));
    }

    [Fact]
    public async Task FileOps_InsideSearchBlocks_AreNotTreatedAsCommands()
    {
        var text = Block("README.md", "A demo app", "DELETE: src/Core/Unrelated.cs\nA demo app");
        var r = await _e.PrepareAsync(text);
        Assert.Single(r.Changes);
        Assert.Equal(EditKind.Modify, r.Changes[0].Kind);
    }

    [Fact]
    public async Task OperationOnSameFileAsEdit_IsRejected()
    {
        var text = Block("src/Core/Unrelated.cs", "Banana", "Cherry") + "\nDELETE: src/Core/Unrelated.cs\n";
        var r = await _e.PrepareAsync(text);
        Assert.Single(r.Changes);
        Assert.Single(r.Problems);
    }

    [Fact]
    public async Task ForgedDeleteNotPrepared_Refused()
    {
        var forged = new FileChange(Guid.NewGuid(), "src/Core/Unrelated.cs", EditKind.Delete, "", "", Array.Empty<DiffLine>(), 0, 0, "x");
        await Assert.ThrowsAsync<ProjectException>(() => _e.ApplyAsync(forged));
    }

    // ---- persistence + restore ----

    [Fact]
    public async Task Store_RoundTrips_PendingAndApplied()
    {
        var store = new SqlitePendingEditStore(_db);
        var conv = Guid.NewGuid();
        var pending = new StoredEdit(Guid.NewGuid(), conv, Guid.NewGuid(), _p.Root, "a.cs", EditKind.Modify, "old", "new", "HASH", null, null);
        var applied = new StoredEdit(Guid.NewGuid(), conv, Guid.NewGuid(), _p.Root, "b.cs", EditKind.Rename, "x", "x", "H2", "c.cs",
            new AppliedInfo("c.cs", EditKind.Rename, "H2", "bk", "b.cs"));
        await store.SaveAsync(pending);
        await store.SaveAsync(applied);

        var list = await new SqlitePendingEditStore(_db).ListAsync(conv); // a new instance = simulated restart
        Assert.Equal(2, list.Count);
        Assert.Equal("new", list.Single(x => x.Id == pending.Id).NewContent);
        Assert.Null(list.Single(x => x.Id == pending.Id).Applied);
        var a = list.Single(x => x.Id == applied.Id);
        Assert.Equal("b.cs", a.Applied!.OldRelativePath);
        Assert.Equal("c.cs", a.NewPath);

        await store.DeleteAsync(pending.Id);
        Assert.Single(await store.ListAsync(conv));
        await store.DeleteForConversationAsync(conv);
        Assert.Empty(await store.ListAsync(conv));
        Assert.Empty(await store.ListAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Restore_PendingEdit_AfterRestart_CanBeApplied()
    {
        var c = await PrepareOne(Block("src/Core/Unrelated.cs", "Banana", "Cherry"));
        var restartedEdits = new EditService(_w, _backup); // no memory of the proposal

        await Assert.ThrowsAsync<ProjectException>(() => restartedEdits.ApplyAsync(Strip(c)));
        var outcome = await restartedEdits.RestoreAsync(Strip(c), _p.Root, null);
        Assert.Equal(RestoreStatus.Pending, outcome.Status);
        Assert.True(outcome.Change!.Added > 0); // diff rebuilt from saved contents

        await restartedEdits.ApplyAsync(outcome.Change);
        Assert.Contains("Cherry", File.ReadAllText(Abs("src/Core/Unrelated.cs")));
    }

    [Fact]
    public async Task Restore_StaleFile_IsNotApplicable()
    {
        var c = await PrepareOne(Block("src/Core/Unrelated.cs", "Banana", "Cherry"));
        File.AppendAllText(Abs("src/Core/Unrelated.cs"), "// changed while app was closed\n");
        var restarted = new EditService(_w, _backup);
        var outcome = await restarted.RestoreAsync(Strip(c), _p.Root, null);
        Assert.Equal(RestoreStatus.Stale, outcome.Status);
        await Assert.ThrowsAsync<ProjectException>(() => restarted.ApplyAsync(Strip(c)));
        Assert.DoesNotContain("Cherry", File.ReadAllText(Abs("src/Core/Unrelated.cs")));
    }

    [Fact]
    public async Task Restore_CreateWhoseFileNowExists_IsStale()
    {
        var c = await PrepareOne(Block("src/New/Thing.cs", "", "class Thing { }"));
        _p.Write("src/New/Thing.cs", "someone else made it");
        var outcome = await new EditService(_w, _backup).RestoreAsync(Strip(c), _p.Root, null);
        Assert.Equal(RestoreStatus.Stale, outcome.Status);
    }

    [Fact]
    public async Task Restore_DifferentProject_IsRefused()
    {
        var c = await PrepareOne(Block("src/Core/Unrelated.cs", "Banana", "Cherry"));
        var other = new TempProject();
        try
        {
            var w2 = new ProjectWorkspace();
            await w2.OpenAsync(other.Root);
            var e2 = new EditService(w2, _backup);
            var outcome = await e2.RestoreAsync(Strip(c), _p.Root, null);
            Assert.Equal(RestoreStatus.OtherProject, outcome.Status);
            await Assert.ThrowsAsync<ProjectException>(() => e2.ApplyAsync(Strip(c)));
        }
        finally { other.Dispose(); }
    }

    [Fact]
    public async Task Restore_NoProjectOpen_IsRefused()
    {
        var c = await PrepareOne(Block("src/Core/Unrelated.cs", "Banana", "Cherry"));
        var closed = new ProjectWorkspace();
        var outcome = await new EditService(closed, _backup).RestoreAsync(Strip(c), _p.Root, null);
        Assert.Equal(RestoreStatus.OtherProject, outcome.Status);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("../escape.txt")]
    [InlineData("secrets/server.pem")]
    [InlineData("node_modules/x/index.js")]
    public async Task Restore_TamperedPath_IsRefused(string path)
    {
        var forged = new FileChange(Guid.NewGuid(), path, EditKind.Create, "", "evil", Array.Empty<DiffLine>(), 0, 0, null);
        var outcome = await _e.RestoreAsync(forged, _p.Root, null);
        Assert.Equal(RestoreStatus.Stale, outcome.Status);
        await Assert.ThrowsAsync<ProjectException>(() => _e.ApplyAsync(forged));
    }

    [Fact]
    public async Task Restore_Applied_UndoWorksAfterRestart()
    {
        var original = File.ReadAllText(Abs("src/Core/Unrelated.cs"));
        var c = await PrepareOne(Block("src/Core/Unrelated.cs", "Banana", "Cherry"));
        await _e.ApplyAsync(c);
        var info = _e.GetApplied(c.Id)!;

        var restarted = new EditService(_w, _backup);
        var outcome = await restarted.RestoreAsync(Strip(c), _p.Root, info);
        Assert.Equal(RestoreStatus.Applied, outcome.Status);
        await restarted.UndoAsync(outcome.Change!);
        Assert.Equal(original, File.ReadAllText(Abs("src/Core/Unrelated.cs")));
    }

    [Fact]
    public async Task Restore_Applied_FileModifiedSince_CannotUndo()
    {
        var c = await PrepareOne(Block("src/Core/Unrelated.cs", "Banana", "Cherry"));
        await _e.ApplyAsync(c);
        var info = _e.GetApplied(c.Id)!;
        File.AppendAllText(Abs("src/Core/Unrelated.cs"), "// newer work\n");
        var outcome = await new EditService(_w, _backup).RestoreAsync(Strip(c), _p.Root, info);
        Assert.Equal(RestoreStatus.Stale, outcome.Status);
    }

    [Fact]
    public async Task Restore_Applied_BackupOutsideBackupRoot_IsRefused()
    {
        var c = await PrepareOne(Block("src/Core/Unrelated.cs", "Banana", "Cherry"));
        await _e.ApplyAsync(c);
        var info = _e.GetApplied(c.Id)! with { BackupPath = Abs("README.md") };
        var outcome = await new EditService(_w, _backup).RestoreAsync(Strip(c), _p.Root, info);
        Assert.Equal(RestoreStatus.Stale, outcome.Status);
    }

    [Fact]
    public async Task Restore_Applied_DeleteAndRename_UndoAfterRestart()
    {
        var del = await PrepareOne("DELETE: src/Core/Unrelated.cs");
        await _e.ApplyAsync(del);
        var ren = await PrepareOne("RENAME: README.md -> docs/README.md");
        await _e.ApplyAsync(ren);

        var restarted = new EditService(_w, _backup);
        var o1 = await restarted.RestoreAsync(Strip(del), _p.Root, _e.GetApplied(del.Id));
        var o2 = await restarted.RestoreAsync(Strip(ren), _p.Root, _e.GetApplied(ren.Id));
        Assert.Equal(RestoreStatus.Applied, o1.Status);
        Assert.Equal(RestoreStatus.Applied, o2.Status);
        await restarted.UndoAsync(o1.Change!);
        await restarted.UndoAsync(o2.Change!);
        Assert.True(File.Exists(Abs("src/Core/Unrelated.cs")));
        Assert.True(File.Exists(Abs("README.md")));
    }

    [Fact]
    public async Task Reject_NeverTouchesDisk_AndRestoredRecordCanBeDropped()
    {
        var before = File.ReadAllText(Abs("src/Core/Unrelated.cs"));
        var store = new SqlitePendingEditStore(_db);
        var c = await PrepareOne(Block("src/Core/Unrelated.cs", "Banana", "Cherry"));
        var conv = Guid.NewGuid();
        await store.SaveAsync(new StoredEdit(c.Id, conv, Guid.NewGuid(), _p.Root, c.RelativePath, c.Kind, c.OriginalContent, c.NewContent, c.BaseHash, null, null));
        await store.DeleteAsync(c.Id); // what Reject does
        Assert.Empty(await store.ListAsync(conv));
        Assert.Equal(before, File.ReadAllText(Abs("src/Core/Unrelated.cs")));
    }
}
