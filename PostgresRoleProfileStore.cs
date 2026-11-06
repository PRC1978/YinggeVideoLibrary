using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace YinggeVideoLibrary;

// Database-mode person profiles. The document CAS and the profile revision both
// matter: a stale editor must not erase a newer link made in another window.
internal static class PostgresRoleProfileStore
{
    internal sealed record Snapshot(RoleProfileStore.State State, string? DocumentRevision);

    internal static async Task<Snapshot> LoadAsync(string connectionString,
        CancellationToken cancellationToken = default)
    {
        var document = await PostgresLibraryDocumentStore.ReadAsync(
            connectionString, RoleProfileStore.FileName, cancellationToken);
        if (document is null) return new(new RoleProfileStore.State(), null);
        if (document.Content.Length > 96L * 1024 * 1024)
            throw new InvalidDataException("人物档案超过允许大小，未读取。");
        var state = RoleProfileStore.Parse(System.Text.Encoding.UTF8.GetString(document.Content));
        return new(state, document.Revision);
    }

    internal static async Task<Snapshot> SaveAsync(string connectionString, Snapshot baseline,
        RoleProfileStore.State edited, CancellationToken cancellationToken = default)
    {
        RoleProfileStore.ValidateState(edited);
        if (!string.Equals(edited.Revision, baseline.State.Revision, StringComparison.Ordinal))
            throw new InvalidOperationException("人物档案编辑版本已变化，请重新载入。");
        var next = RoleProfileStore.Parse(JsonSerializer.Serialize(edited));
        next.Revision = Guid.NewGuid().ToString("N");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next);
        try
        {
            var revision = await PostgresLibraryDocumentStore.SaveAsync(connectionString,
                RoleProfileStore.FileName, bytes, baseline.DocumentRevision, cancellationToken);
            edited.Revision = next.Revision;
            return new(next, revision);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("人物档案已由另一窗口修改，请重新载入。", ex);
        }
    }

    // A bulk discovery batch may link a person to a work only while the exact
    // metadata snapshot it checked is still authoritative. The metadata
    // revision and role document CAS are checked under the shared library lock.
    internal static async Task<Snapshot> SaveVerifiedBatchAsync(string connectionString,
        Snapshot baseline, RoleProfileStore.State edited, string expectedMetadataRevision,
        CancellationToken cancellationToken = default)
    {
        RoleProfileStore.ValidateState(edited);
        if (string.IsNullOrWhiteSpace(expectedMetadataRevision) ||
            !string.Equals(edited.Revision, baseline.State.Revision, StringComparison.Ordinal))
            throw new InvalidOperationException("人物档案或作品资料版本已变化，请重新预览。 ");
        var next = RoleProfileStore.Parse(JsonSerializer.Serialize(edited));
        next.Revision = Guid.NewGuid().ToString("N");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next);
        var revision = Convert.ToHexString(SHA256.HashData(bytes));
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(178942601)", connection, transaction))
            await gate.ExecuteNonQueryAsync(cancellationToken);
        await using (var marker = new NpgsqlCommand(
            "SELECT metadata_sha256 FROM library_migration WHERE id=1", connection, transaction))
            if (!string.Equals((string?)await marker.ExecuteScalarAsync(cancellationToken),
                    expectedMetadataRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("作品资料已在搜刮期间变化，未保存当前批次。 ");
        var sql = baseline.DocumentRevision is null
            ? "INSERT INTO library_document(name,content,sha256) VALUES (@name,@content,@next) ON CONFLICT DO NOTHING"
            : "UPDATE library_document SET content=@content,sha256=@next WHERE name=@name AND sha256=@prior";
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("name", RoleProfileStore.FileName);
            command.Parameters.AddWithValue("content", NpgsqlDbType.Bytea, bytes);
            command.Parameters.AddWithValue("next", revision);
            if (baseline.DocumentRevision is not null)
                command.Parameters.AddWithValue("prior", baseline.DocumentRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("人物档案已由另一操作修改，未保存当前批次。 ");
        }
        await transaction.CommitAsync(cancellationToken);
        edited.Revision = next.Revision;
        return new(next, revision);
    }
}
