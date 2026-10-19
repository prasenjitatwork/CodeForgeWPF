using System.Globalization;
using System.Text.RegularExpressions;
using CodeForge.Application;
using Microsoft.Data.Sqlite;

namespace CodeForge.Infrastructure.Persistence;

public sealed partial class SqliteMemoryStore : IMemoryStore
{
    public const int MaxNotesPerProject = 50;
    public const int MaxNoteChars = 500;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteMemoryStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
    }

    [GeneratedRegex(@"(?i)(password|passwd|secret|api[_-]?key|access[_-]?key|private[_-]?key|bearer|token)\s*[:=]|\b(sk|ghp|gho|xox[bp])[-_][A-Za-z0-9]{16,}|-----BEGIN [A-Z ]*PRIVATE KEY|AKIA[0-9A-Z]{16}")]
    private static partial Regex SecretLike();

    public static string Key(string root) => Path.TrimEndingDirectorySeparator(root).ToLowerInvariant();

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        if (!_initialized)
        {
            await _initLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_initialized)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        CREATE TABLE IF NOT EXISTS notes(
                            id INTEGER PRIMARY KEY AUTOINCREMENT, project TEXT NOT NULL, text TEXT NOT NULL, created_at TEXT NOT NULL);
                        CREATE INDEX IF NOT EXISTS ix_notes_project ON notes(project);
                        CREATE TABLE IF NOT EXISTS plans(
                            message_id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, title TEXT NOT NULL, steps TEXT NOT NULL);
                        CREATE INDEX IF NOT EXISTS ix_plans_conv ON plans(conversation_id);
                        """;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    _initialized = true;
                }
            }
            finally { _initLock.Release(); }
        }
        return conn;
    }

    public async Task<IReadOnlyList<MemoryNote>> ListNotesAsync(string projectRoot, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, text, created_at FROM notes WHERE project=$p ORDER BY id";
        cmd.Parameters.AddWithValue("$p", Key(projectRoot));
        var list = new List<MemoryNote>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new MemoryNote(r.GetInt64(0), r.GetString(1),
                DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        return list;
    }

    public async Task<MemoryNote> AddNoteAsync(string projectRoot, string text, CancellationToken ct = default)
    {
        text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (text.Length == 0) throw new ProjectException("The note is empty.");
        if (text.Length > MaxNoteChars) throw new ProjectException($"Notes are limited to {MaxNoteChars} characters.");
        if (SecretLike().IsMatch(text)) throw new ProjectException("That looks like a password or key. Secrets are never saved to memory.");

        var existing = await ListNotesAsync(projectRoot, ct).ConfigureAwait(false);
        if (existing.Any(n => n.Text.Equals(text, StringComparison.OrdinalIgnoreCase))) throw new ProjectException("That note is already saved.");
        if (existing.Count >= MaxNotesPerProject) throw new ProjectException($"Memory is full ({MaxNotesPerProject} notes). Delete one first.");

        var now = DateTimeOffset.UtcNow;
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO notes(project, text, created_at) VALUES($p,$t,$c); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p", Key(projectRoot));
        cmd.Parameters.AddWithValue("$t", text);
        cmd.Parameters.AddWithValue("$c", now.ToString("O"));
        var id = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        return new MemoryNote(id, text, now);
    }

    public async Task DeleteNoteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM notes WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearNotesAsync(string projectRoot, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM notes WHERE project=$p";
        cmd.Parameters.AddWithValue("$p", Key(projectRoot));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SavePlanAsync(Guid conversationId, StoredPlan plan, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO plans(message_id, conversation_id, title, steps) VALUES($m,$c,$t,$s)
            ON CONFLICT(message_id) DO UPDATE SET title=$t, steps=$s;
            """;
        cmd.Parameters.AddWithValue("$m", plan.MessageId.ToString());
        cmd.Parameters.AddWithValue("$c", conversationId.ToString());
        cmd.Parameters.AddWithValue("$t", plan.Title);
        cmd.Parameters.AddWithValue("$s", System.Text.Json.JsonSerializer.Serialize(plan.Steps));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredPlan>> LoadPlansAsync(Guid conversationId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT message_id, title, steps FROM plans WHERE conversation_id=$c";
        cmd.Parameters.AddWithValue("$c", conversationId.ToString());
        var list = new List<StoredPlan>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var steps = System.Text.Json.JsonSerializer.Deserialize<List<StoredPlanStep>>(r.GetString(2));
                if (steps is { Count: > 0 }) list.Add(new StoredPlan(Guid.Parse(r.GetString(0)), r.GetString(1), steps));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException) { /* skip corrupt row */ }
        }
        return list;
    }

    public async Task DeletePlansAsync(Guid conversationId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM plans WHERE conversation_id=$c";
        cmd.Parameters.AddWithValue("$c", conversationId.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
