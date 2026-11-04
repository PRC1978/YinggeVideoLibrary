using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

// Only the HTTPS endpoint is stored here. The session token lives in Windows
// Credential Manager; nothing in this file can be replayed as a login.
internal sealed class CloudAccountSettings
{
    public string ServerUrl { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured =>
        Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(uri.Host);

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
