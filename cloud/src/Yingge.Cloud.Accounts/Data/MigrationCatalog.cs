using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Yingge.Cloud.Accounts.Data;

internal sealed record MigrationDefinition(
    string Version,
    string Name,
    string UpSql,
    string DownSql,
    string UpChecksum);

// Migrations are embedded SQL files named <version>_<name>.up.sql/.down.sql.
// Both directions are versioned and checksummed so an operator can audit what
// was applied and roll back a known version without a migration tool.
internal static class MigrationCatalog
{
    const string Prefix = "Yingge.Cloud.Accounts.Migrations.";

    public static IReadOnlyList<MigrationDefinition> Load()
    {
        var assembly = typeof(MigrationCatalog).Assembly;
        var up = new Dictionary<string, string>(StringComparer.Ordinal);
        var down = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            var remainder = resource[Prefix.Length..];
            // <version>_<name>.up.sql
            var parts = remainder.Split('.');
            if (parts.Length != 3 || parts[2] != "sql") continue;
            var key = parts[0];
            var direction = parts[1];
            var sql = ReadResource(assembly, resource);
            if (direction == "up") up[key] = sql;
            else if (direction == "down") down[key] = sql;
        }

        var migrations = new List<MigrationDefinition>();
        foreach (var (key, upSql) in up)
        {
            var separator = key.IndexOf('_');
            var version = separator > 0 ? key[..separator] : key;
            var name = separator > 0 ? key[(separator + 1)..] : key;
            migrations.Add(new MigrationDefinition(version, name, upSql,
                down.TryGetValue(key, out var downSql) ? downSql : "",
                Checksum(upSql)));
        }

        return migrations.OrderBy(migration => migration.Version, StringComparer.Ordinal).ToArray();
    }

    static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("找不到迁移资源：" + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static string Checksum(string sql)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sql));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
