using System.IO;
using System.Text.Json;

namespace YinggeVideoLibrary;

internal sealed class ErrorUploadSettings
{
    public string Target { get; set; } = "";
    public string Port { get; set; } = "22";
    public string KeyPath { get; set; } = "";

    static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YinggeVideoLibrary");
    internal static string FilePath => Path.Combine(DirectoryPath, "error-upload-settings.json");

    internal static ErrorUploadSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new ErrorUploadSettings();
            var settings = JsonSerializer.Deserialize<ErrorUploadSettings>(File.ReadAllText(FilePath));
            return settings is not null && SshEncryptedReportUploader.ValidTarget(settings.Target) &&
                SshEncryptedReportUploader.ValidPort(settings.Port) &&
                !string.IsNullOrWhiteSpace(settings.KeyPath) ? settings : new ErrorUploadSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new ErrorUploadSettings(); }
    }

    internal void Save()
    {
        if (!SshEncryptedReportUploader.ValidTarget(Target) || !SshEncryptedReportUploader.ValidPort(Port) ||
            !Path.IsPathFullyQualified(KeyPath) || !File.Exists(KeyPath))
            throw new InvalidDataException("服务器、端口或 SSH 私钥路径无效。 ");
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
