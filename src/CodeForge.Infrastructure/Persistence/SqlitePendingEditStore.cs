using CodeForge.Application;
using Microsoft.Data.Sqlite;

namespace CodeForge.Infrastructure.Persistence;

/// <summary>Keeps edit proposals (and the info needed to undo applied ones) so they can be reviewed after a restart. Local file only.</summary>
public sealed class SqlitePendingEditStore : IPendingEditStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqlitePendingEditStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
    }

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
                        CREATE TABLE IF NOT EXISTS pending_edits(
                            id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, message_id TEXT NOT NULL, project_root TEXT NOT NULL,
                            rel_path TEXT NOT NULL, kind TEXT NOT NULL, original TEXT NOT NULL, new_content TEXT NOT NULL,
                            base_hash TEXT NULL, new_path TEXT NULL,
                            applied_rel TEXT NULL, applied_kind TEXT NULL, applied_hash TEXT NULL, backup_path TEXT NULL, old_rel TEXT NULL,
                            created_at TEXT NOT NULL);
                        CREATE INDEX IF NOT EXISTS ix_pending_conv ON pending_edits(conversation_id);
                        """;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    _initialized = true;
                }
            }
            finally { _initLock.Release(); }
        }
        return conn;
    }

    private static object Db(string? v) => v is null ? DBNull.Value : v;

    public async Task SaveAsync(StoredEdit e, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pending_edits(id, conversation_id, message_id, project_root, rel_path, kind, original, new_content, base_hash, new_path,
                                      applied_rel, applied_kind, applied_hash, backup_path, old_rel, created_at)
            VALUES($id,$c,$m,$root,$rel,$kind,$o,$n,$bh,$np,$ar,$ak,$ah,$bp,$or,$t)
            ON CONFLICT(id) DO UPDATE SET applied_rel=$ar, applied_kind=$ak, applied_hash=$ah, backup_path=$bp, old_rel=$or;
            """;
        cmd.Parameters.AddWithValue("$id", e.Id.ToString());
        cmd.Parameters.AddWithValue("$c", e.ConversationId.ToString());
        cmd.Parameters.AddWithValue("$m", e.MessageId.ToString());
        cmd.Parameters.AddWithValue("$root", e.ProjectRoot);
        cmd.Parameters.AddWithValue("$rel", e.RelativePath);
        cmd.Parameters.AddWithValue("$kind", e.Kind.ToString());
        cmd.Parameters.AddWithValue("$o", e.OriginalContent);
        cmd.Parameters.AddWithValue("$n", e.NewContent);
        cmd.Parameters.AddWithValue("$bh", Db(e.BaseHash));
        cmd.Parameters.AddWithValue("$np", Db(e.NewPath));
        cmd.Parameters.AddWithValue("$ar", Db(e.Applied?.RelativePath));
        cmd.Parameters.AddWithValue("$ak", Db(e.Applied?.Kind.ToString()));
        cmd.Parameters.AddWithValue("$ah", Db(e.Applied?.AppliedHash));
        cmd.Parameters.AddWithValue("$bp", Db(e.Applied?.BackupPath));
        cmd.Parameters.AddWithValue("$or", Db(e.Applied?.OldRelativePath));
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredEdit>> ListAsync(Guid conversationId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, message_id, project_root, rel_path, kind, original, new_content, base_hash, new_path,
                   applied_rel, applied_kind, applied_hash, backup_path, old_rel
            FROM pending_edits WHERE conversation_id=$c ORDER BY created_at, rowid
            """;
        cmd.Parameters.AddWithValue("$c", conversationId.ToString());
        var list = new List<StoredEdit>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            try
            {
                AppliedInfo? applied = null;
                if (!r.IsDBNull(9) && !r.IsDBNull(10) && !r.IsDBNull(11))
                    applied = new AppliedInfo(r.GetString(9), Enum.Parse<EditKind>(r.GetString(10)), r.GetString(11),
                        r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13));
                list.Add(new StoredEdit(Guid.Parse(r.GetString(0)), conversationId, Guid.Parse(r.GetString(1)), r.GetString(2),
                    r.GetString(3), Enum.Parse<EditKind>(r.GetString(4)), r.GetString(5), r.GetString(6),
                    r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), applied));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { /* skip corrupt row */ }
        }
        return list;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pending_edits WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteForConversationAsync(Guid conversationId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pending_edits WHERE conversation_id=$c";
        cmd.Parameters.AddWithValue("$c", conversationId.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
