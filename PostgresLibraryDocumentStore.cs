using System.IO;
using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;

namespace YinggeVideoLibrary;

// Byte-preserving storage for user-state files while their typed PostgreSQL
// writers are migrated. Normal UI writes must not use this independently of
// index, metadata, review, and backup cutover.
internal static class PostgresLibraryDocumentStore
{
    internal sealed record Snapshot(byte[] Content, string Revision);

    internal static async Task<Snapshot?> ReadAsync(string connectionString, string name,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT content, sha256 FROM library_document WHERE name=@name", connection);
        command.Parameters.AddWithValue("name", name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var bytes = (byte[])reader[0];
        var revision = reader.GetString(1);
        if (revision != Hash(bytes)) throw new InvalidDataException(
            "数据库资料校验失败：" + name);
        return new(bytes, revision);
    }

    internal static async Task<string> SaveAsync(string connectionString, string name,
        byte[] content, string? expectedRevision, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(content);
        var next = Hash(content);
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(178942601)", connection, transaction))
            await gate.ExecuteNonQueryAsync(cancellationToken);

        // Null means create-only. Never silently replace an existing document.
        var sql = expectedRevision is null
            ? "INSERT INTO library_document(name,content,sha256) VALUES (@name,@content,@next) ON CONFLICT DO NOTHING"
            : "UPDATE library_document SET content=@content,sha256=@next WHERE name=@name AND sha256=@expected";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("content", NpgsqlDbType.Bytea, content);
        command.Parameters.AddWithValue("next", next);
        if (expectedRevision is not null) command.Parameters.AddWithValue("expected", expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("资料已由另一操作修改，请重新载入后再保存：" + name);
        await transaction.CommitAsync(cancellationToken);
        return next;
    }

    internal static async Task DeleteAsync(string connectionString, string name,
        string expectedRevision, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        if (string.IsNullOrWhiteSpace(expectedRevision)) throw new ArgumentException(
            "必须提供期望版本。", nameof(expectedRevision));
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(178942601)", connection, transaction))
            await gate.ExecuteNonQueryAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM library_document WHERE name=@name AND sha256=@expected", connection, transaction);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("expected", expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("资料已由另一操作修改，请重新载入后再删除：" + name);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private static void ValidateName(string name)
    {
        if (!PostgresLibraryMigration.IsDocumentName(name))
            throw new InvalidDataException("数据库资料名称不在允许范围内。 ");
    }
}
