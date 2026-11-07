using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Only the HTTPS endpoint is stored here. The session token lives in Windows
// Credential Manager; nothing in this file can be replayed as a login.
internal sealed class CloudAccountSettings
{
    public string ServerUrl { get; set; } = "https://reports.yingevideo.top";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured =>
        Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(uri.Host) &&
        uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 &&
        uri.Fragment.Length == 0;

    // Credentials and queued reports must stay with the endpoint that created
    // them. A changed server URL must never receive another server's token.
    internal string EndpointKey
    {
        get
        {
            if (!IsConfigured) throw new InvalidOperationException("请先填写 HTTPS 服务器地址。");
            var uri = new Uri(ServerUrl.Trim(), UriKind.Absolute);
            var origin = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin)))[..24];
        }
    }

    internal static string FilePath => Path.Combine(AppPaths.DataDirectory, "cloud-account.json");

    internal static CloudAccountSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new CloudAccountSettings();
            var settings = JsonSerializer.Deserialize<CloudAccountSettings>(File.ReadAllText(FilePath));
            return settings ?? new CloudAccountSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                             or JsonException)
        {
            return new CloudAccountSettings();
        }
    }

    internal void Save()
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
