using System.Globalization;
using CodeForge.Application;
using CodeForge.Domain;
using Microsoft.Data.Sqlite;

namespace CodeForge.Infrastructure.Persistence;

public sealed class SqliteConversationRepository : IConversationRepository
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteConversationRepository(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
    }

    private static DateTimeOffset ParseDate(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

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
                        CREATE TABLE IF NOT EXISTS conversations(
                            id TEXT PRIMARY KEY, title TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                        CREATE TABLE IF NOT EXISTS messages(
                            id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                            role INTEGER NOT NULL, content TEXT NOT NULL, created_at TEXT NOT NULL, seq INTEGER NOT NULL);
                        CREATE INDEX IF NOT EXISTS ix_messages_conv ON messages(conversation_id, seq);
                        """;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    _initialized = true;
                }
            }
            finally { _initLock.Release(); }
        }
        await using (var fk = conn.CreateCommand())
        {
            fk.CommandText = "PRAGMA foreign_keys = ON;";
            await fk.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return conn;
    }

    public async Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, created_at, updated_at FROM conversations ORDER BY updated_at DESC";
        var result = new List<Conversation>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            result.Add(new Conversation
            {
                Id = Guid.Parse(r.GetString(0)),
                Title = r.GetString(1),
                CreatedAt = ParseDate(r.GetString(2)),
                UpdatedAt = ParseDate(r.GetString(3))
            });
        return result;
    }

    public async Task<Conversation?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        Conversation? conv = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT title, created_at, updated_at FROM conversations WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
                conv = new Conversation
                {
                    Id = id,
                    Title = r.GetString(0),
                    CreatedAt = ParseDate(r.GetString(1)),
                    UpdatedAt = ParseDate(r.GetString(2))
                };
        }
        if (conv is null) return null;

        await using var mc = conn.CreateCommand();
        mc.CommandText = "SELECT id, role, content, created_at FROM messages WHERE conversation_id = $id ORDER BY seq";
        mc.Parameters.AddWithValue("$id", id.ToString());
        await using var mr = await mc.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await mr.ReadAsync(ct).ConfigureAwait(false))
            conv.Messages.Add(new ChatMessage
            {
                Id = Guid.Parse(mr.GetString(0)),
                Role = (ChatRole)mr.GetInt32(1),
                Content = mr.GetString(2),
                CreatedAt = ParseDate(mr.GetString(3))
            });
        return conv;
    }

    public async Task SaveAsync(Conversation c, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO conversations(id, title, created_at, updated_at) VALUES($id,$t,$c,$u)
                ON CONFLICT(id) DO UPDATE SET title=$t, updated_at=$u;
                DELETE FROM messages WHERE conversation_id=$id;
                """;
            cmd.Parameters.AddWithValue("$id", c.Id.ToString());
            cmd.Parameters.AddWithValue("$t", c.Title);
            cmd.Parameters.AddWithValue("$c", c.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$u", c.UpdatedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var seq = 0;
        foreach (var m in c.Messages)
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO messages(id, conversation_id, role, content, created_at, seq) VALUES($id,$cid,$r,$ct,$c,$s)";
            ins.Parameters.AddWithValue("$id", m.Id.ToString());
            ins.Parameters.AddWithValue("$cid", c.Id.ToString());
            ins.Parameters.AddWithValue("$r", (int)m.Role);
            ins.Parameters.AddWithValue("$ct", m.Content);
            ins.Parameters.AddWithValue("$c", m.CreatedAt.ToString("O"));
            ins.Parameters.AddWithValue("$s", seq++);
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM conversations WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
