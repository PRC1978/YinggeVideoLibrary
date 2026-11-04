namespace Yingge.Cloud.Client;

// The session token is never written to a plain configuration file. The
// desktop host implements this over the Windows credential manager; tests use
// the in-memory store.
public interface ISessionStore
{
    ValueTask<string?> ReadAsync(CancellationToken ct = default);

    ValueTask WriteAsync(string token, CancellationToken ct = default);

    ValueTask ClearAsync(CancellationToken ct = default);
}

public sealed class InMemorySessionStore(string? initial = null) : ISessionStore
{
    string? token = initial;

    public ValueTask<string?> ReadAsync(CancellationToken ct = default) => ValueTask.FromResult(token);

    public ValueTask WriteAsync(string value, CancellationToken ct = default)
    {
        token = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken ct = default)
    {
        token = null;
        return ValueTask.CompletedTask;
    }
}

public sealed class NullSessionStore : ISessionStore
{
    public ValueTask<string?> ReadAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<string?>(null);

    public ValueTask WriteAsync(string token, CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask ClearAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
}
