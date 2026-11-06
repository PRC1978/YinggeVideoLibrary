using Npgsql;

namespace YinggeVideoLibrary;

// Session-level advisory lock: held for the entire worker loop, unlike the
// short transaction lock used by individual queue mutations.
internal sealed class PostgresScrapeWorkerLease : IAsyncDisposable
{
    private const long WorkerLock = 178942602;
    private readonly NpgsqlDataSource source;
    private readonly NpgsqlConnection connection;
    private bool disposed;

    private PostgresScrapeWorkerLease(NpgsqlDataSource source, NpgsqlConnection connection)
    {
        this.source = source;
        this.connection = connection;
    }

    internal static async Task<PostgresScrapeWorkerLease?> TryAcquireAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        var source = NpgsqlDataSource.Create(connectionString);
        NpgsqlConnection? connection = null;
        try
        {
            connection = await source.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"SELECT pg_try_advisory_lock({WorkerLock})", connection);
            var acquired = (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
            if (acquired) return new(source, connection);
            await connection.DisposeAsync();
            await source.DisposeAsync();
            return null;
        }
        catch
        {
            if (connection is not null) await connection.DisposeAsync();
            await source.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (connection.FullState == System.Data.ConnectionState.Open)
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT pg_advisory_unlock({WorkerLock})", connection);
                await command.ExecuteScalarAsync();
            }
        }
        finally
        {
            await connection.DisposeAsync();
            await source.DisposeAsync();
        }
    }
}
