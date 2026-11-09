using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Buffers.Binary;

namespace Yingge.Cloud.Tests;

public sealed class AvatarTests(PostgresFixture fixture) : CloudTestBase(fixture)
{
    const string Path = "/api/v1/account/avatar";

    [Fact]
    public async Task Avatar_is_private_replaceable_and_deletable()
    {
        var alice = await CreateAccountAsync();
        var bob = await CreateAccountAsync();
        var red = Png(240, 20, 20);
        var blue = Png(20, 20, 240);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(alice.Token, red)).StatusCode);
        Assert.Equal(red, await GetBytesAsync(alice.Token));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, bob.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(alice.Token, blue)).StatusCode);
        Assert.Equal(blue, await GetBytesAsync(alice.Token));
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(bob.Token, red)).StatusCode);
        Assert.Equal(blue, await GetBytesAsync(alice.Token));
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, alice.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, alice.Token)).StatusCode);
        Assert.Equal(red, await GetBytesAsync(bob.Token));
        Assert.Equal(1, await Fixture.ScalarAsync<int>("select count(*)::int from account_avatars"));
    }

    [Fact]
    public async Task Avatar_rejects_unauthenticated_unsupported_and_bad_payloads()
    {
        var alice = await CreateAccountAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutAsync(null, Png(1, 2, 3))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType,
            (await PutAsync(alice.Token, Png(1, 2, 3), "image/jpeg")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await PutAsync(alice.Token, [1, 2, 3])).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await PutAsync(alice.Token, new byte[240 * 1024 + 1])).StatusCode);
        Assert.Equal(0, await Fixture.ScalarAsync<int>("select count(*)::int from account_avatars"));
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string? token)
    {
        using var request = new HttpRequestMessage(method, Path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await Client.SendAsync(request);
    }

    async Task<HttpResponseMessage> PutAsync(string? token, byte[] data, string mime = "image/png")
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new ByteArrayContent(data);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        return await Client.SendAsync(request);
    }

    async Task<byte[]> GetBytesAsync(string token)
    {
        using var response = await SendAsync(HttpMethod.Get, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return await response.Content.ReadAsByteArrayAsync();
    }

    internal static byte[] Png(byte r, byte g, byte b)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), 1);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk(output, "IHDR", ihdr);
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zip.Write([0, r, g, b, 255]);
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)data.Length);
        output.Write(size);
        var name = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(name);
        output.Write(data);
        var crc = 0xffffffffu;
        foreach (var value in name.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        crc = ~crc;
        BinaryPrimitives.WriteUInt32BigEndian(size, crc);
        output.Write(size);
    }
}
