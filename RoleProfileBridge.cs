using System.Text.Json;

namespace YinggeVideoLibrary;

// Keeps the role editor on one storage boundary in both desktop modes.
internal sealed class RoleProfileBridge(string dataDir, PostgresLibraryRuntimeSession? postgres)
{
    internal bool DatabaseMode => postgres is not null;

    internal async Task<RoleProfileStore.State> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (postgres is null)
            return await Task.Run(() => RoleProfileStore.Load(dataDir), cancellationToken);
        await postgres.ReloadRolesAsync(cancellationToken);
        return RoleProfileStore.Parse(JsonSerializer.Serialize(postgres.Roles.State));
    }

    internal async Task SaveAsync(RoleProfileStore.State state,
        CancellationToken cancellationToken = default)
    {
        if (postgres is null)
        {
            await Task.Run(() => RoleProfileStore.Save(dataDir, state), cancellationToken);
            return;
        }
        await postgres.SaveRolesAsync(state, cancellationToken);
    }

    internal async Task<Dictionary<string, MovieMetadata>> LoadMetadataAsync(
        CancellationToken cancellationToken = default)
    {
        if (postgres is null)
            return await Task.Run(() => MetadataStore.Load(dataDir), cancellationToken);
        var snapshot = await PostgresLibraryMetadataStore.ReadAsync(postgres.ConnectionString,
            cancellationToken);
        return snapshot.Records.ToDictionary(item => item.Key, item => item.Value,
            StringComparer.OrdinalIgnoreCase);
    }
}
