using Npgsql;
using NpgsqlTypes;

namespace Yingge.Cloud.Accounts.Data;

internal sealed class AvatarRepository(NpgsqlDataSource db)
{
    public async Task<byte[]?> GetAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "select image_png from account_avatars where account_id = @account_id", connection);
        command.Parameters.Add(Db.Parameter("account_id", NpgsqlDbType.Uuid, accountId));
        return await command.ExecuteScalarAsync(ct) as byte[];
    }

    public async Task PutAsync(Guid accountId, byte[] png, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            insert into account_avatars (account_id, image_png) values (@account_id, @image_png)
            on conflict (account_id) do update set image_png = excluded.image_png, updated_at = now()
            """, connection);
        command.Parameters.Add(Db.Parameter("account_id", NpgsqlDbType.Uuid, accountId));
        command.Parameters.Add(Db.Parameter("image_png", NpgsqlDbType.Bytea, png));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "delete from account_avatars where account_id = @account_id", connection);
        command.Parameters.Add(Db.Parameter("account_id", NpgsqlDbType.Uuid, accountId));
        await command.ExecuteNonQueryAsync(ct);
    }
}
