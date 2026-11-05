using System.IO;
using System.Text.Json;

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
}
